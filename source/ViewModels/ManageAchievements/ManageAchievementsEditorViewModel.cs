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
using System.Reflection;
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

        private bool _isDetailsPaneExpanded = true;
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
        private bool _providerBaselinesResolved;
        private bool _isTogglingReveal;
        private bool _hasCustomOrder;
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
        private readonly HashSet<string> _selectedCategoryFilters =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _selectedTypeFilters =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _selectedCustomizationFilters =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private List<string> _categoryFilterOptions = new List<string>();
        private bool _canRevealAnyTitle;
        private bool _canRevealAnyDescription;
        private bool _canRevealAnyTrophy;
        private bool _canRevealAnyPoints;
        private bool _canRevealAnyIcon;
        private bool _areAllTitlesRevealed = true;
        private bool _areAllDescriptionsRevealed = true;
        private bool _areAllTrophiesRevealed = true;
        private bool _areAllPointsRevealed = true;
        private AchievementIconRevealStage _iconColumnStage = AchievementIconRevealStage.Unlocked;
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

            // Every edit this tab makes is already stored by the time the user sees it, so the
            // history is built from the writes themselves rather than from an uncommitted buffer.
            _gameCustomDataStore.CustomDataWritten += GameCustomDataStore_CustomDataWritten;
            UndoCommand = new RelayCommand(_ => Undo(), _ => CanUndo && !IsSaving);
            RedoCommand = new RelayCommand(_ => Redo(), _ => CanRedo && !IsSaving);

            CustomProviderOptions = new ObservableCollection<CustomProviderOption>();
            AddCustomProviderCommand = new RelayCommand(_ => AddCustomProvider(), _ => IsCustomOnlyGame && _customProviderStore != null && !IsSaving);
            EditCustomProviderCommand = new RelayCommand(_ => EditCustomProvider(), _ => HasSelectedCustomProvider && _showEditor != null && !IsSaving);

            AchievementRows = new ObservableCollection<AchievementEditorRow>();
            CategoryFilter = BuildCategoryFilter();
            TypeFilter = BuildTypeFilter();
            CustomizationFilter = BuildCustomizationFilter();
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
            ResetOrderCommand = new RelayCommand(_ => ResetOrder(), _ => HasCustomOrder && !IsSaving);
            AutoCapstoneCommand = new RelayCommand(_ => _ = ApplyAutoCapstoneAsync(), _ => HasRows && !IsSaving);
            ToggleAllTitlesRevealCommand = new RelayCommand(_ => ToggleAllTitlesReveal());
            ToggleAllDescriptionsRevealCommand = new RelayCommand(_ => ToggleAllDescriptionsReveal());
            ToggleAllTrophiesRevealCommand = new RelayCommand(_ => ToggleAllTrophiesReveal());
            ToggleAllPointsRevealCommand = new RelayCommand(_ => ToggleAllPointsReveal());
            CycleAllIconStagesCommand = new RelayCommand(_ => CycleAllIconStages());

            ReloadData();
        }

        public event EventHandler CustomAchievementsSaved;

        /// <summary>Raised after a category or type assignment was persisted for a custom row.</summary>
        public event EventHandler AssignmentsChanged;

        /// <summary>
        /// Raised after icon overrides were written, carrying the ApiNames whose icon moved. The
        /// override maps alone only reach the surfaces that hydrate over them; this is what carries
        /// the new art into the cached rows and the theme state, the same as the Icons tab.
        /// </summary>
        public event EventHandler<IconOverridesSavedEventArgs> IconOverridesSaved;

        /// <summary>
        /// Raised after a rebuild replaced the rows, carrying the ApiNames that were selected before
        /// it. SelectedItems lives on the control, so only the view can put a multi-row selection
        /// back; the single-row case is restored here through <see cref="SelectedRow"/>.
        /// </summary>
        public event EventHandler<IReadOnlyList<string>> RestoreSelectionRequested;

        /// <summary>
        /// Asks the grid to bring a row into view. Raised when the editor picks a row on the user's
        /// behalf, which is the one case where the row they should be looking at may be off screen.
        /// </summary>
        public event EventHandler<AchievementEditorRow> ScrollRowIntoViewRequested;

        /// <summary>Raised after the game's manual capstone was changed from this tab.</summary>
        public event EventHandler<CapstoneChangedEventArgs> CapstoneChanged;

        /// <summary>
        /// The plugin settings this tab was built against, for the view's own persisted state
        /// (the grid's column layout) rather than anything the rows carry.
        /// </summary>
        internal PlayniteAchievementsSettings Settings => _settings;

        #region Undo history

        private readonly EditorUndoJournal _undoJournal = new EditorUndoJournal();

        /// <summary>
        /// The gesture the writes arriving right now belong to. Set at each user-facing entry
        /// point and cleared when the step is closed, so the history can tell a fan-out across
        /// twenty rows from twenty separate edits.
        /// </summary>
        private EditorEditIntent _currentUndoIntent;

        /// <summary>
        /// The achievements the current gesture is touching, for reselecting them after an undo.
        /// </summary>
        private readonly List<string> _currentUndoApiNames = new List<string>();

        private DispatcherTimer _undoStepTimer;

        /// <summary>
        /// Set while a step is being reversed, so the write that reverses it is not itself
        /// recorded as a new step.
        /// </summary>
        private bool _isApplyingUndo;

        public RelayCommand UndoCommand { get; }

        public RelayCommand RedoCommand { get; }

        public bool CanUndo => _undoJournal.CanUndo || _undoJournal.HasOpenStep;

        public bool CanRedo => _undoJournal.CanRedo;

        /// <summary>The gesture undo would reverse, for the button's tooltip.</summary>
        public string UndoDescription => DescribeHistoryStep(
            "LOCPlayAch_ManageAchievements_Editor_UndoFormat",
            _undoJournal.UndoLabelKey,
            "LOCPlayAch_Common_Undo");

        public string RedoDescription => DescribeHistoryStep(
            "LOCPlayAch_ManageAchievements_Editor_RedoFormat",
            _undoJournal.RedoLabelKey,
            "LOCPlayAch_Common_Redo");

        /// <summary>
        /// Names the gesture a history button would act on. Falls back to the bare verb while the
        /// step being collected has no name yet.
        /// </summary>
        private static string DescribeHistoryStep(string formatKey, string labelKey, string fallbackKey)
        {
            var verb = ResourceProvider.GetString(fallbackKey);
            if (string.IsNullOrWhiteSpace(labelKey))
            {
                return verb;
            }

            var label = ResourceProvider.GetString(labelKey);
            return string.IsNullOrWhiteSpace(label)
                ? verb
                : string.Format(ResourceProvider.GetString(formatKey), label);
        }

        /// <summary>
        /// Says which gesture the writes that follow belong to.
        /// </summary>
        /// <remarks>
        /// A label and a grouping boundary, never a capture: the writes themselves are recorded by
        /// the store's own event, so a site that forgets to call this splits one gesture into two
        /// undo steps rather than losing it. That asymmetry is the point - the history cannot be
        /// made wrong by a missing call here.
        ///
        /// Not an IDisposable scope, because the saves this tab issues are fire-and-forget: a
        /// using block would close before the write it was labelling had happened.
        /// </remarks>
        /// <summary>
        /// The gesture a changed property belongs to.
        /// </summary>
        /// <remarks>
        /// Usually the property itself, so editing points and then trophy is two steps. The
        /// exception is the unlock timestamp: setting it raises the date, the time text, the mode
        /// and the has-a-time flag together, and those are one gesture rather than four.
        /// </remarks>
        private static string ResolveFieldGesture(string propertyName)
        {
            switch (propertyName)
            {
                case nameof(AchievementEditorRow.UnlockTime):
                case nameof(AchievementEditorRow.UnlockDate):
                case nameof(AchievementEditorRow.TimeText):
                case nameof(AchievementEditorRow.SelectedTimeModeText):
                case nameof(AchievementEditorRow.HasUnlockTime):
                    return nameof(AchievementEditorRow.UnlockTime);

                // Both sides of the progress pair read as one edit.
                case nameof(AchievementEditorRow.ProgressNumText):
                case nameof(AchievementEditorRow.ProgressDenomText):
                    return "Progress";

                default:
                    return propertyName;
            }
        }

        private void MarkUndoIntent(EditorEditIntent intent, IEnumerable<string> apiNames = null)
        {
            if (_isApplyingUndo)
            {
                return;
            }

            if (intent != null && !intent.Equals(_currentUndoIntent))
            {
                // A new gesture closes the one before it, so the step boundary is the gesture
                // boundary rather than a matter of timing.
                CommitUndoStep();
            }

            _currentUndoIntent = intent;
            _currentUndoApiNames.Clear();
            if (apiNames != null)
            {
                _currentUndoApiNames.AddRange(apiNames.Where(name => !string.IsNullOrWhiteSpace(name)));
            }

            if (_currentUndoApiNames.Count == 0)
            {
                _currentUndoApiNames.AddRange(
                    ResolveSelectionTargets().Select(row => row.OriginalApiName));
            }
        }

        /// <summary>
        /// The per-achievement fields an undo can put back one at a time.
        /// </summary>
        /// <remarks>
        /// Only the fields that live on the per-achievement override record. The assignments -
        /// category, type, goal, filter, capstone - are written as whole lists rebuilt from every
        /// row, so a single field is not the unit there and they are reversed from the record.
        /// </remarks>
        /// <summary>
        /// The row properties a change to which is actually stored.
        /// </summary>
        /// <remarks>
        /// A gesture is only named for these. An edit also raises derived properties - whether the
        /// row has changes, what its customization markers are - and naming a gesture after one of
        /// those closed the real step and opened a second under a name nothing meant, which the
        /// write that followed then landed in. One edit became two undo steps, the second of them
        /// record-shaped and slow.
        /// </remarks>
        private static readonly HashSet<string> PersistedRowProperties =
            new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(AchievementEditorRow.DisplayName),
                nameof(AchievementEditorRow.Description),
                nameof(AchievementEditorRow.PointsText),
                nameof(AchievementEditorRow.TrophyType),
                nameof(AchievementEditorRow.Hidden),
                nameof(AchievementEditorRow.AchievementNote),
                nameof(AchievementEditorRow.RarityInput),
                nameof(AchievementEditorRow.ProgressNumText),
                nameof(AchievementEditorRow.ProgressDenomText),
                nameof(AchievementEditorRow.UnlockedIconPath),
                nameof(AchievementEditorRow.LockedIconPath),
                nameof(AchievementEditorRow.Unlocked),
                nameof(AchievementEditorRow.UnlockTime),
                nameof(AchievementEditorRow.HasUnlockTime),
                nameof(AchievementEditorRow.UnlockDate),
                nameof(AchievementEditorRow.TimeText),
                nameof(AchievementEditorRow.SelectedTimeModeText),
                nameof(AchievementEditorRow.CategoryLabel),
                nameof(AchievementEditorRow.CategoryTypeValue),
                nameof(AchievementEditorRow.IsCapstone),
                nameof(AchievementEditorRow.IsGoal),
                nameof(AchievementEditorRow.IsFiltered),
                nameof(AchievementEditorRow.IsSummaryFiltered)
            };

        private static readonly IReadOnlyDictionary<string, PropertyInfo> UndoableRowFields =
            new[]
            {
                nameof(AchievementEditorRow.DisplayName),
                nameof(AchievementEditorRow.Description),
                nameof(AchievementEditorRow.PointsText),
                nameof(AchievementEditorRow.TrophyType),
                nameof(AchievementEditorRow.Hidden),
                nameof(AchievementEditorRow.AchievementNote),
                nameof(AchievementEditorRow.RarityInput),
                nameof(AchievementEditorRow.ProgressNumText),
                nameof(AchievementEditorRow.ProgressDenomText),
                nameof(AchievementEditorRow.UnlockedIconPath),
                nameof(AchievementEditorRow.LockedIconPath),
                nameof(AchievementEditorRow.Unlocked),
                nameof(AchievementEditorRow.UnlockTime)
            }
            .ToDictionary(
                name => name,
                name => typeof(AchievementEditorRow).GetProperty(name),
                StringComparer.Ordinal);

        /// <summary>
        /// Notes what a field held before an edit replaced it.
        /// </summary>
        /// <remarks>
        /// Runs from the row's setter, which is earlier than the property-changed hook and the
        /// only point where the old value still exists. It therefore also names the gesture: by
        /// the time the property-changed hook runs the value is already gone.
        /// </remarks>
        private void RecordRowValueChange(
            AchievementEditorRow row,
            string propertyName,
            object oldValue,
            object newValue)
        {
            if (_isApplyingUndo ||
                row == null ||
                string.IsNullOrWhiteSpace(row.OriginalApiName) ||
                !UndoableRowFields.ContainsKey(propertyName ?? string.Empty))
            {
                return;
            }

            var intent = EditorEditIntent.FieldEdit(ResolveFieldGesture(propertyName), "LOCPlayAch_Common_Edit");
            MarkUndoIntent(intent);

            if (IsIconField(propertyName))
            {
                // Remembered as copies of the art, not as the paths. Replacing art writes over
                // the same managed filename, so the paths on both sides are identical and the
                // image on the old side is gone - neither side is a value that survives.
                oldValue = RetainIconValue(oldValue);
                newValue = RetainIconValue(newValue);
            }

            _undoJournal.RecordRowValue(row.OriginalApiName, propertyName, oldValue, newValue, intent);
        }

        private void GameCustomDataStore_CustomDataWritten(object sender, GameCustomDataWrittenEventArgs e)
        {
            if (e == null || e.PlayniteGameId != _gameId)
            {
                return;
            }

            if (_isApplyingUndo)
            {
                // The write that reverses a step is not a step of its own.
                return;
            }

            _undoJournal.Record(
                e.Previous,
                e.Persisted,
                e.AffectsSummaryData,
                e.AffectsOverrideMirror,
                _currentUndoIntent,
                _currentUndoApiNames);

            RestartUndoStepTimer();
            RaiseHistoryState();
        }

        /// <summary>
        /// Closes the open step once the writes stop arriving.
        /// </summary>
        /// <remarks>
        /// The delay matches the one the plugin already coalesces custom-data notifications on, so
        /// an undo boundary lands where the rest of the plugin already treats a burst as finished.
        /// It also covers the gap a fire-and-forget save leaves between the override write and the
        /// definition write that follows it.
        /// </remarks>
        private void RestartUndoStepTimer()
        {
            if (_undoStepTimer == null)
            {
                _undoStepTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(400)
                };
                _undoStepTimer.Tick += UndoStepTimer_Tick;
            }

            _undoStepTimer.Stop();
            _undoStepTimer.Start();
        }

        private void UndoStepTimer_Tick(object sender, EventArgs e)
        {
            _undoStepTimer?.Stop();
            if (IsSaving)
            {
                // A save still in flight means more writes are coming for this gesture.
                RestartUndoStepTimer();
                return;
            }

            CommitUndoStep();
        }

        private void CommitUndoStep()
        {
            _undoStepTimer?.Stop();
            _undoJournal.CommitOpenStep();
            _currentUndoIntent = null;
            _currentUndoApiNames.Clear();
            RaiseHistoryState();
        }

        private void Undo()
        {
            ApplyHistoryStep(_undoJournal.Undo(), reverse: true);
        }

        private void Redo()
        {
            ApplyHistoryStep(_undoJournal.Redo(), reverse: false);
        }

        /// <summary>
        /// Writes a history step's values back through the store.
        /// </summary>
        /// <remarks>
        /// Through the store's own update, with the flags the original write reported, so the
        /// whole downstream cascade - the cache mirror, the theme state, the tag sync, the
        /// overview projections - runs for the reversal exactly as it ran for the edit. Writing
        /// the repository directly would put the record back and leave every one of those stale.
        /// </remarks>
        private void ApplyHistoryStep(EditorUndoEntry entry, bool reverse)
        {
            if (entry == null)
            {
                return;
            }

            // A field edit is reversed by setting the field back, through the same setter the
            // edit used. That makes the undo an edit: it writes what an edit writes, costs what
            // an edit costs, and cannot disturb a field it did not record.
            if (entry.IsRowValueStep)
            {
                ApplyRowValueStep(entry, reverse);
                return;
            }

            // Before the write, not after it. The host skips the refresh its own cascade brings
            // back only if this marker is already set when it arrives, and the cascade starts
            // inside the write below - so leaving it to the debounced notification the way an
            // ordinary edit does would let a second rebuild of every row through per press.
            SuppressExternalRefresh = true;

            _isApplyingUndo = true;
            try
            {
                using (Common.PerfScope.Start(_logger, "Editor.HistoryStep.Write", thresholdMs: 10))
                {
                    _gameCustomDataStore.Update(
                    _gameId,
                    data =>
                    {
                        // Reverse order when undoing: a gesture that wrote several facets in turn
                        // may have derived a later one from an earlier one.
                        var facets = reverse ? entry.Facets.Reverse() : entry.Facets;
                        foreach (var patch in facets)
                        {
                            if (reverse)
                            {
                                patch.ApplyBefore(data);
                            }
                            else
                            {
                                patch.ApplyAfter(data);
                            }
                        }
                    },
                    entry.AffectsSummaryData,
                    entry.AffectsOverrideMirror);
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to {(reverse ? "undo" : "redo")} an editor change for gameId={_gameId}.");
                SetStatus(string.Format(L("LOCPlayAch_Status_Failed", "Error: {0}"), ex.Message), true);
                _undoJournal.Clear();
                RaiseHistoryState();
                return;
            }
            finally
            {
                _isApplyingUndo = false;
            }

            // A full reload re-hydrates the game and rebuilds every row, which is most of what a
            // press of the shortcut costs. It is only needed when the step moved something the
            // rows cannot be re-seeded from in place.
            if (RequiresReloadAfterHistoryStep(entry))
            {
                using (Common.PerfScope.Start(_logger, "Editor.HistoryStep.Reload", thresholdMs: 10))
                {
                    ReloadData();
                }
            }
            else
            {
                using (Common.PerfScope.Start(_logger, "Editor.HistoryStep.Reseed", thresholdMs: 10))
                {
                    ReseedRowsAfterHistoryStep();
                }
            }

            RaiseAssignmentsChanged();
            RaiseHistoryState();

            if (entry.AffectedApiNames.Count > 0)
            {
                RestoreSelectionRequested?.Invoke(this, entry.AffectedApiNames.ToList());
            }
        }

        /// <summary>
        /// Whether reversing this step needs the game reloaded, or whether the rows already on
        /// screen can be re-seeded in place.
        /// </summary>
        /// <remarks>
        /// The rows carry their own values while the tab is open - that is why an ordinary edit
        /// never reloads - so a facet the rows can be re-seeded from is cheap to reverse. Three
        /// kinds are not: the authored definitions add and remove rows, the two orders decide
        /// which row sits where, and the rest are game-level state the rows do not carry.
        ///
        /// The per-achievement override record is re-seeded too. Its effective value for a field
        /// is the override where there is one and the provider's own otherwise, and the row
        /// already carries both, so putting one back needs no hydration.
        /// </remarks>
        private bool RequiresReloadAfterHistoryStep(EditorUndoEntry entry)
        {
            foreach (var patch in entry.Facets)
            {
                switch (patch.Facet)
                {
                    case GameCustomDataFacet.Capstones:
                    case GameCustomDataFacet.CategoryOverrides:
                    case GameCustomDataFacet.CategoryTypeOverrides:
                    case GameCustomDataFacet.GoalApiNames:
                    case GameCustomDataFacet.FilteredApiNames:
                    case GameCustomDataFacet.SummaryFilteredApiNames:
                        continue;

                    // The per-achievement override record reloads, and must keep doing so.
                    //
                    // Re-seeding it from the row looks possible - the row carries the provider's
                    // value beside its own - but writing the provider's value into the row's own
                    // field makes the row claim it as the user's. The icon maps are then rebuilt
                    // from the rows on the next write, which stamps every provider icon in as an
                    // override and marks the whole game customized. Hydration is what knows the
                    // difference between a value a row holds and one it merely displays.
                    case GameCustomDataFacet.AchievementOverrides:
                        return true;

                    default:
                        return true;
                }
            }

            return entry.Facets.Count == 0;
        }

        /// <summary>
        /// Puts the rows back in step with the store without reloading the game.
        /// </summary>
        private void ReseedRowsAfterHistoryStep()
        {
            var resolved = ResolveCurrentCustomData();
            var goals = new HashSet<string>(
                resolved?.GoalAchievementApiNames ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            var filtered = new HashSet<string>(
                resolved?.FilteredAchievementApiNames ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            var summaryFiltered = new HashSet<string>(
                resolved?.SummaryFilteredAchievementApiNames ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            foreach (var row in AchievementRows)
            {
                var apiName = NormalizeText(row?.OriginalApiName);
                if (row == null || string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                // From the source, so putting a value back does not read as a fresh edit and
                // write itself out again.
                row.SetGoalFromSource(goals.Contains(apiName));
                row.SetFilterScopeFromSource(
                    filtered.Contains(apiName)
                        ? AchievementFilterScope.All
                        : summaryFiltered.Contains(apiName)
                            ? AchievementFilterScope.Summary
                            : AchievementFilterScope.None);

            }

            // Covers the category, the type and the capstone, and refreshes the pickers with them.
            // Handed the record already resolved above rather than resolving it a second time.
            RefreshAssignmentState(resolved);
            RefreshComputedState();
            SyncBulkRowFromSelection();
        }

        /// <summary>
        /// Puts each field in a step back to the value it held, one row at a time.
        /// </summary>
        /// <remarks>
        /// No store write and no reload here: assigning the property runs the row's own hook,
        /// which persists it exactly as it does for an edit. That is the whole point - an undo
        /// that is the same operation as the edit is the same cost by construction, rather than
        /// being made fast by a second mechanism that has to be kept in step with the first.
        /// </remarks>
        private void ApplyRowValueStep(EditorUndoEntry entry, bool reverse)
        {
            var byApiName = AchievementRows
                .Where(row => row != null && !string.IsNullOrWhiteSpace(row.OriginalApiName))
                .GroupBy(row => row.OriginalApiName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            // Set while the values go back, so the writes this causes are not recorded as a new
            // step and the gesture label is left alone.
            _isApplyingUndo = true;
            try
            {
                using (Common.PerfScope.Start(_logger, "Editor.HistoryStep.RowValues", thresholdMs: 5))
                {
                    foreach (var change in entry.RowValues)
                    {
                        if (!byApiName.TryGetValue(change.ApiName, out var row) ||
                            !UndoableRowFields.TryGetValue(change.PropertyName, out var property) ||
                            property == null)
                        {
                            continue;
                        }

                        // An icon step already holds copies of the art on both sides, taken when
                        // the change was recorded, so there is nothing to resolve here: writing
                        // the copy re-materializes it exactly as picking a file would.
                        property.SetValue(row, reverse ? change.OldValue : change.NewValue);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to {(reverse ? "undo" : "redo")} a field edit for gameId={_gameId}.");
                SetStatus(string.Format(L("LOCPlayAch_Status_Failed", "Error: {0}"), ex.Message), true);
                _undoJournal.Clear();
            }
            finally
            {
                _isApplyingUndo = false;
            }

            RaiseHistoryState();

            // Deliberately no reselection. The rows never moved - only values on them changed -
            // so re-applying the selection would only make the highlight flicker on a row that
            // was already selected.
        }

        #region Retained icon art

        /// <summary>
        /// Where art that an undo is about to orphan is kept for the rest of the session.
        /// </summary>
        /// <remarks>
        /// Icons are the one thing in the history that is a file rather than a value. Undoing an
        /// icon override removes the record, and the store prunes the managed art along with it -
        /// so a redo that only replayed the path would be pointing at a file that no longer
        /// exists, which is what made redo silently do nothing.
        ///
        /// A copy is taken before the undo writes, and redo materializes from that copy. It lives
        /// under the system temp directory for the life of this editor, and goes with it.
        /// </remarks>
        private string _retainedIconRoot;

        /// <summary>How many copies have been taken, so each one gets its own name.</summary>
        private int _retainedIconCount;

        /// <summary>
        /// The value an icon change should be remembered by: a copy of the art it points at.
        /// </summary>
        /// <remarks>
        /// The managed path is a slot, not a value. Replacing art writes the new image over the
        /// same managed filename, so both sides of that change are the same string - which the
        /// history reads as nothing having happened, and which would leave the old image gone
        /// regardless. A source path is no better: it is usually a temp file that will not be
        /// there later.
        ///
        /// A fresh copy is taken every time, deliberately. Reusing a copy already taken for the
        /// same path is what limited this to one level: the managed slot keeps its name while its
        /// contents change, so every replacement resolved back to the first image copied.
        /// </remarks>
        private object RetainIconValue(object value)
        {
            var path = NormalizeText(value as string);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                // Blank stays blank: that is a clear, and there is nothing to keep.
                return value;
            }

            try
            {
                if (string.IsNullOrWhiteSpace(_retainedIconRoot))
                {
                    _retainedIconRoot = Path.Combine(
                        Path.GetTempPath(),
                        "PlayniteAchievements",
                        "undo-icons",
                        Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(_retainedIconRoot);
                }

                var copy = Path.Combine(
                    _retainedIconRoot,
                    (_retainedIconCount++).ToString(CultureInfo.InvariantCulture) +
                        Path.GetExtension(path));
                File.Copy(path, copy, overwrite: true);
                return copy;
            }
            catch (Exception ex)
            {
                // A step that cannot restore its art is a poor outcome, but not one worth failing
                // the edit over. The path is kept, which still restores it while the file lasts.
                _logger?.Warn(ex, $"Could not keep a copy of icon art for the history: {path}");
                return value;
            }
        }

        private void DiscardRetainedIconArt()
        {
            _retainedIconCount = 0;
            if (string.IsNullOrWhiteSpace(_retainedIconRoot))
            {
                return;
            }

            try
            {
                if (Directory.Exists(_retainedIconRoot))
                {
                    Directory.Delete(_retainedIconRoot, recursive: true);
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Could not remove retained icon art at {_retainedIconRoot}.");
            }

            _retainedIconRoot = null;
        }

        private static bool IsIconField(string propertyName)
        {
            return string.Equals(propertyName, nameof(AchievementEditorRow.UnlockedIconPath), StringComparison.Ordinal) ||
                string.Equals(propertyName, nameof(AchievementEditorRow.LockedIconPath), StringComparison.Ordinal);
        }

        #endregion

        private void RaiseHistoryState()
        {
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
            OnPropertyChanged(nameof(UndoDescription));
            OnPropertyChanged(nameof(RedoDescription));
            RaiseCommandStates();
        }

        #endregion

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

        /// <summary>
        /// Drops the stored achievement order, putting the list back the way the providers hand it
        /// over. Disabled until there is an order to drop.
        /// </summary>
        public RelayCommand ResetOrderCommand { get; }

        /// <summary>
        /// Points the game's capstone at its platinum trophy, authoring one when the game has none.
        /// </summary>
        public RelayCommand AutoCapstoneCommand { get; }

        /// <summary>
        /// Reveals every masked name in the grid, or masks them all again when none is left masked.
        /// Acts on the rows the filter is showing: the column header is part of what is on screen,
        /// so it should not quietly reveal hundreds of rows the user cannot see.
        /// </summary>
        public RelayCommand ToggleAllTitlesRevealCommand { get; }

        public RelayCommand ToggleAllDescriptionsRevealCommand { get; }

        public RelayCommand ToggleAllTrophiesRevealCommand { get; }

        public RelayCommand ToggleAllPointsRevealCommand { get; }

        /// <summary>
        /// Steps the whole icon column through the hidden placeholder, the locked placeholder and
        /// the achievements' own art, skipping whichever of those the rows on screen do not have.
        /// </summary>
        public RelayCommand CycleAllIconStagesCommand { get; }

        /// <summary>
        /// Whether any row on screen has something to reveal for that column. The header toggle is
        /// shown only where there is, matching the per-row buttons.
        /// </summary>
        /// <summary>
        /// The column headers' summary of the rows on screen, recomputed as a single pass by
        /// <see cref="RefreshRevealHeaderState"/> rather than per property.
        /// </summary>
        /// <remarks>
        /// These were nine computed properties over <see cref="VisibleRows"/>, and raising them
        /// together ran the filter over every row nine times. On a game whose grid lists a whole
        /// library that is what a filter change was spending its time on.
        /// </remarks>
        public bool CanRevealAnyTitle => _canRevealAnyTitle;

        public bool CanRevealAnyDescription => _canRevealAnyDescription;

        public bool CanRevealAnyTrophy => _canRevealAnyTrophy;

        public bool CanRevealAnyPoints => _canRevealAnyPoints;

        public bool CanRevealAnyIcon => _canRevealAnyIcon;

        /// <summary>
        /// Whether nothing maskable is left masked in that column, which is what turns the header
        /// toggle back into a re-mask.
        /// </summary>
        public bool AreAllTitlesRevealed => _areAllTitlesRevealed;

        public bool AreAllDescriptionsRevealed => _areAllDescriptionsRevealed;

        public bool AreAllTrophiesRevealed => _areAllTrophiesRevealed;

        public bool AreAllPointsRevealed => _areAllPointsRevealed;

        /// <summary>
        /// The stage the icon column's toggle shows: the most masked one any row on screen is still
        /// at, so the button describes the column rather than whichever row happens to be first.
        /// </summary>
        public AchievementIconRevealStage IconColumnStage => _iconColumnStage;

        private AchievementIconRevealStage ComputeIconColumnStage(IReadOnlyList<AchievementEditorRow> visible)
        {
            var stage = int.MaxValue;
            foreach (var row in visible)
            {
                if (row.CanReveal && (int)row.IconStage < stage)
                {
                    stage = (int)row.IconStage;
                }
            }

            return stage == int.MaxValue
                ? AchievementIconRevealStage.Unlocked
                : (AchievementIconRevealStage)stage;
        }

        public bool IconColumnStageIsCovered => IconColumnStage == AchievementIconRevealStage.Covered;

        public bool IconColumnStageIsLocked => IconColumnStage == AchievementIconRevealStage.Locked;

        public bool IconColumnStageIsUnlocked => IconColumnStage == AchievementIconRevealStage.Unlocked;

        /// <summary>The rows the grid is currently showing, in grid order.</summary>
        private IEnumerable<AchievementEditorRow> VisibleRows =>
            AchievementRows.Where(row => row != null && MatchesFilter(row));

        private void ToggleAllTitlesReveal() =>
            ToggleAllReveal(row => row.CanRevealTitle, row => row.IsTitleRevealed, (row, value) => row.IsTitleRevealed = value);

        private void ToggleAllDescriptionsReveal() =>
            ToggleAllReveal(row => row.CanRevealDescription, row => row.IsDescriptionRevealed, (row, value) => row.IsDescriptionRevealed = value);

        private void ToggleAllTrophiesReveal() =>
            ToggleAllReveal(row => row.CanRevealTrophy, row => row.IsTrophyRevealed, (row, value) => row.IsTrophyRevealed = value);

        private void ToggleAllPointsReveal() =>
            ToggleAllReveal(row => row.CanRevealPoints, row => row.IsPointsRevealed, (row, value) => row.IsPointsRevealed = value);

        /// <summary>
        /// Steps every maskable row on screen to the column's next stage. Each row settles on the
        /// nearest stage it has, so a column holding both hidden and merely locked achievements
        /// still moves as one.
        /// </summary>
        private void CycleAllIconStages()
        {
            using var perfScope = Common.PerfScope.Start(
                _logger,
                "Editor.CycleAllIconStages",
                thresholdMs: 10,
                context: "rows=" + AchievementRows.Count);

            var targets = VisibleRows.Where(row => row.CanReveal).ToList();
            if (targets.Count == 0)
            {
                return;
            }

            var next = IconColumnStage == AchievementIconRevealStage.Unlocked
                ? AchievementIconRevealStage.Covered
                : IconColumnStage + 1;

            _isTogglingReveal = true;
            try
            {
                foreach (var row in targets)
                {
                    row.IconStage = next;
                }
            }
            finally
            {
                _isTogglingReveal = false;
            }

            RefreshRevealHeaderState();
        }

        /// <summary>
        /// Reveals every maskable row on screen, or masks them all again once none is left masked,
        /// so one header click always has a visible effect.
        /// </summary>
        private void ToggleAllReveal(
            Func<AchievementEditorRow, bool> canReveal,
            Func<AchievementEditorRow, bool> isRevealed,
            Action<AchievementEditorRow, bool> setRevealed)
        {
            var targets = VisibleRows.Where(canReveal).ToList();
            if (targets.Count == 0)
            {
                return;
            }

            var reveal = targets.Any(row => !isRevealed(row));
            // The rows raise their reveal state one at a time and the header reads every row, so
            // the recompute is held until the walk is done rather than paid once per row.
            _isTogglingReveal = true;
            try
            {
                foreach (var row in targets)
                {
                    setRevealed(row, reveal);
                }
            }
            finally
            {
                _isTogglingReveal = false;
            }

            RefreshRevealHeaderState();
        }

        /// <summary>Re-reads the three column toggles from the rows on screen.</summary>
        private void RefreshRevealHeaderState()
        {
            // One materialized pass over the filtered rows feeds every summary below. The filter
            // predicate is the expensive part, so it must run once per refresh, not once per
            // property.
            var visible = VisibleRows.ToList();
            _canRevealAnyTitle = false;
            _canRevealAnyDescription = false;
            _canRevealAnyTrophy = false;
            _canRevealAnyPoints = false;
            _canRevealAnyIcon = false;
            _areAllTitlesRevealed = true;
            _areAllDescriptionsRevealed = true;
            _areAllTrophiesRevealed = true;
            _areAllPointsRevealed = true;
            foreach (var row in visible)
            {
                if (row.CanRevealTitle)
                {
                    _canRevealAnyTitle = true;
                    if (!row.IsTitleRevealed)
                    {
                        _areAllTitlesRevealed = false;
                    }
                }

                if (row.CanRevealDescription)
                {
                    _canRevealAnyDescription = true;
                    if (!row.IsDescriptionRevealed)
                    {
                        _areAllDescriptionsRevealed = false;
                    }
                }

                if (row.CanRevealTrophy)
                {
                    _canRevealAnyTrophy = true;
                    if (!row.IsTrophyRevealed)
                    {
                        _areAllTrophiesRevealed = false;
                    }
                }

                if (row.CanRevealPoints)
                {
                    _canRevealAnyPoints = true;
                    if (!row.IsPointsRevealed)
                    {
                        _areAllPointsRevealed = false;
                    }
                }

                if (row.CanReveal)
                {
                    _canRevealAnyIcon = true;
                }
            }

            _iconColumnStage = ComputeIconColumnStage(visible);

            OnPropertyChanged(nameof(CanRevealAnyTitle));
            OnPropertyChanged(nameof(CanRevealAnyDescription));
            OnPropertyChanged(nameof(CanRevealAnyTrophy));
            OnPropertyChanged(nameof(CanRevealAnyPoints));
            OnPropertyChanged(nameof(CanRevealAnyIcon));
            OnPropertyChanged(nameof(AreAllTitlesRevealed));
            OnPropertyChanged(nameof(AreAllDescriptionsRevealed));
            OnPropertyChanged(nameof(AreAllTrophiesRevealed));
            OnPropertyChanged(nameof(AreAllPointsRevealed));
            OnPropertyChanged(nameof(IconColumnStage));
            OnPropertyChanged(nameof(IconColumnStageIsCovered));
            OnPropertyChanged(nameof(IconColumnStageIsLocked));
            OnPropertyChanged(nameof(IconColumnStageIsUnlocked));
        }

        private static bool IsRevealStateProperty(string propertyName)
        {
            switch (propertyName)
            {
                case nameof(AchievementEditorRow.IconStage):
                case nameof(AchievementEditorRow.IsIconCovered):
                case nameof(AchievementEditorRow.IsIconStageLocked):
                case nameof(AchievementEditorRow.IsIconStageUnlocked):
                case nameof(AchievementEditorRow.IsTitleRevealed):
                case nameof(AchievementEditorRow.IsDescriptionRevealed):
                case nameof(AchievementEditorRow.IsTitleHidden):
                case nameof(AchievementEditorRow.IsDescriptionHidden):
                case nameof(AchievementEditorRow.IsTrophyRevealed):
                case nameof(AchievementEditorRow.IsPointsRevealed):
                case nameof(AchievementEditorRow.IsTrophyHidden):
                case nameof(AchievementEditorRow.IsPointsHidden):
                case nameof(AchievementEditorRow.CanReveal):
                case nameof(AchievementEditorRow.CanRevealTitle):
                case nameof(AchievementEditorRow.CanRevealDescription):
                case nameof(AchievementEditorRow.CanRevealTrophy):
                case nameof(AchievementEditorRow.CanRevealPoints):
                    return true;
                default:
                    return false;
            }
        }

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
                    OnPropertyChanged(nameof(IsCapstoneEditableForSelection));
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
        /// Whether this row is one of the rows an edit would currently apply to. A cell asks
        /// before routing its edit to <see cref="EditTarget"/>, because a control can keep focus
        /// after its own row has dropped out of the selection.
        /// </summary>
        public bool IsRowInSelection(AchievementEditorRow row)
        {
            return row != null && (_selectedRows.Contains(row) || ReferenceEquals(row, SelectedRow));
        }

        /// <summary>
        /// Whether a capstone edit would be accepted for the current selection.
        /// </summary>
        /// <remarks>
        /// <see cref="SetCapstoneForSelection"/> refuses a selection that spans categories, since
        /// one capstone cannot stand for several. A cell's button reads this so the refusal shows
        /// as a disabled control rather than a click that silently does nothing.
        /// </remarks>
        public bool IsCapstoneEditableForSelection => EditTarget?.CanEditCapstone == true;

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

        /// <summary>
        /// Whether the details pane is showing. Deliberately not persisted: folding it away is a
        /// gesture for the width of one piece of work, so the tab opens showing the pane every time
        /// rather than hiding the editors from someone who does not remember collapsing them.
        /// </summary>
        public bool IsDetailsPaneExpanded
        {
            get => _isDetailsPaneExpanded;
            set => SetValue(ref _isDetailsPaneExpanded, value);
        }

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
            using var perfScope = Common.PerfScope.Start(
                _logger,
                "Editor.SetSelectedRows",
                thresholdMs: 10,
                context: "rows=" + AchievementRows.Count);

            var incoming = (rows ?? Enumerable.Empty<AchievementEditorRow>())
                .Where(row => row != null)
                .ToList();

            // The grid re-reports the same selection while it processes a collection reset and
            // again after a view refresh. Each report otherwise raises EditTarget and re-seeds the
            // whole details pane, which is most of the cost of one edit.
            if (incoming.Count == _selectedRows.Count &&
                !incoming.Where((row, index) => !ReferenceEquals(row, _selectedRows[index])).Any())
            {
                return;
            }

            _selectedRows.Clear();
            foreach (var row in incoming)
            {
                _selectedRows.Add(row);
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
            OnPropertyChanged(nameof(IsCapstoneEditableForSelection));
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
            row.CategoryLabel = SharedValue(r => r.EffectiveCategoryLabel);
            row.CategoryTypeValue = SharedValue(r => r.EffectiveCategoryTypeValue);
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

            // Several achievements can become capstones in one go only while they sit in different
            // categories. Two in the same category would displace each other as the write walked
            // the selection, leaving one capstone and no sign of which.
            row.AllowBulkCapstone = SelectionCategoriesAreDistinct();
            row.SetCapstoneStateFromSource(SharedCapstone(), null, null, null);
            row.SuppressNotifications = false;

            row.PropertyChanged += BulkRow_PropertyChanged;
            BulkRow = row;
        }

        /// <summary>
        /// Whether every selected achievement sits in a different category, which is what makes a
        /// capstone edit across the selection mean something.
        /// </summary>
        private bool SelectionCategoriesAreDistinct()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in _selectedRows)
            {
                if (row == null || string.IsNullOrWhiteSpace(row.OriginalApiName))
                {
                    continue;
                }

                if (!seen.Add(AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(
                        row.EffectiveCategoryLabel)))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// The scope the whole selection agrees on, or None when it does not.
        /// </summary>
        /// <summary>Whether every selected achievement is a capstone.</summary>
        private bool SharedCapstone()
        {
            var any = false;
            foreach (var row in _selectedRows)
            {
                if (row == null)
                {
                    continue;
                }

                any = true;
                if (!row.IsCapstone)
                {
                    return false;
                }
            }

            return any;
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
                _bulkRow.CategoryLabel = SharedValue(r => r.EffectiveCategoryLabel);
                _bulkRow.CategoryTypeValue = SharedValue(r => r.EffectiveCategoryTypeValue);
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

        /// <summary>Whether this game carries a stored achievement order.</summary>
        public bool HasCustomOrder
        {
            get => _hasCustomOrder;
            private set
            {
                if (SetValueAndReturn(ref _hasCustomOrder, value))
                {
                    ResetOrderCommand.RaiseCanExecuteChanged();
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

        /// <remarks>
        /// The None option's value is the empty string, and a bulk selection that disagrees carries
        /// a null trophy type - which matches no option, so the combo renders blank. Do not coalesce
        /// that null to string.Empty anywhere on the way to this combo: it would select None and
        /// push it two-way onto every selected row.
        /// </remarks>
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
            using var reloadScope = Common.PerfScope.Start(
                _logger,
                "Editor.ReloadData",
                thresholdMs: 25);

            // Names each post-pass so a slow open points at one of them instead of at the whole
            // reload. Declared here rather than as a method so it closes over nothing but the
            // logger, and it costs nothing when tracing is off: PerfScope.Start returns null.
            void Row(string tag, Action pass)
            {
                using (Common.PerfScope.Start(
                    _logger,
                    "Editor.ReloadData." + tag,
                    thresholdMs: 10,
                    context: "rows=" + AchievementRows.Count))
                {
                    pass();
                }
            }

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
                    GameAchievementData hydrated;
                    using (Common.PerfScope.Start(_logger, "Editor.ReloadData.Hydrate", thresholdMs: 5))
                    {
                        hydrated = _gameDataSnapshotProvider?.GetHydratedGameData();
                    }

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

                // One scope per pass. Every one of these walks the row set, so each scales with
                // the achievement count, and a reported "opening the editor with hundreds of
                // achievements lags" could not be attributed to any of them: only the hydrate and
                // the row swap were measured, and the whole of ReloadData was not. The row count
                // rides on the outer scope so cost per row is readable.
                // Resolved once for the rest of the load. Each resolve deep-clones this game's
                // whole record and rebuilds a dozen collections, and the load used to pay for two
                // of them -- one here and one for HasCustomOrder below. RefreshAssignmentState
                // already takes a pre-resolved record for exactly this reason.
                var resolvedCustomData = ResolveCurrentCustomData();

                Row("CaptureCollectionBaseline", CaptureCollectionBaseline);
                Row("ApplyAutoCapstoneMarker", () => ApplyAutoCapstoneMarker(data));
                Row("ApplyProviderBaselines", ApplyProviderBaselines);
                Row("RefreshAssignmentState", () => RefreshAssignmentState(resolvedCustomData));
                Row("RefreshCustomProviderState", RefreshCustomProviderState);
                Row("ApplyManualTrackingToRows", ApplyManualTrackingToRows);
                Row("RebuildSearchIndex", RebuildSearchIndex);
                Row("RebuildFilterOptions", RebuildFilterOptions);
                Row("SeedOverrideWriteCache", SeedOverrideWriteCache);
                HasCustomOrder = resolvedCustomData?.AchievementOrder?.Count > 0;
                Row("RefreshRevealHeaderState", RefreshRevealHeaderState);
                SetStatus(null, false);
                Row("RefreshComputedState", RefreshComputedState);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed loading custom achievements for gameId={_gameId}.");
                ReplaceRows(Array.Empty<AchievementEditorRow>());
                SetStatus(string.Format(L("LOCPlayAch_Status_Failed", "Error: {0}"), ex.Message), true);
            }
        }

        /// <summary>
        /// Marks the row standing for the auto capstone, the rows being built from achievements
        /// rather than from the definitions that carry the mark.
        /// </summary>
        private void ApplyAutoCapstoneMarker(GameCustomDataFile data)
        {
            var marked = data?.CustomAchievements?.FirstOrDefault(definition => definition?.IsAutoCapstone == true);
            var apiName = marked == null
                ? null
                : CustomAchievementProjectionService.BuildApiName(marked.Id);
            foreach (var row in AchievementRows)
            {
                if (row != null)
                {
                    row.IsAutoCapstone = !string.IsNullOrWhiteSpace(apiName) &&
                                         string.Equals(row.OriginalApiName, apiName, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        /// <summary>
        /// Stamps each row with the provider's own values and the managed-cache file stem, read
        /// from the raw snapshot because the rows themselves show the overridden values. This is
        /// what lets a row tell an edited field from one the provider supplied, for the icon
        /// override writes and for the grid's customization marker alike.
        /// </summary>
        private void ApplyProviderBaselines()
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
            _providerBaselinesResolved = rawByApiName.Count > 0;

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
                    row.ProviderUnlockedIconPath = ResolveProviderIconBaseline(
                        raw.UnlockedIconPath,
                        row.IconFileStem,
                        AchievementIconVariant.Unlocked);
                    row.ProviderLockedIconPath = ResolveProviderIconBaseline(
                        raw.LockedIconPath,
                        row.IconFileStem,
                        AchievementIconVariant.Locked);
                    row.ProviderHidden = raw.Hidden;
                    // The hydrated row carries the overridden type, so the provider's own is only
                    // available here. Without it an existing type override would compare equal to
                    // "what the provider says" and be dropped on the next write.
                    row.ProviderCategoryTypeValue = raw.CategoryType;
                    row.ProviderDisplayName = raw.DisplayName;
                    row.ProviderDescription = raw.Description;
                    row.ProviderPoints = raw.Points;
                    row.ProviderTrophyType = raw.TrophyType;
                    row.ProviderUnlockTimeUtc = raw.UnlockTimeUtc;
                    row.ProviderIsCapstone = raw.IsCapstone;
                    row.MarkProviderBaselinesKnown();
                }
                else if (!row.IsProviderRow)
                {
                    // An authored achievement has no raw entry to compare against and needs none:
                    // it is the user's own outright.
                    row.MarkProviderBaselinesKnown();
                }

                // The load runs with notifications suppressed and the baselines above are plain
                // setters, so the row's own hook has not seen any of this.
                row.RefreshCustomizationState();
            }
        }

        /// <summary>
        /// The provider's own art for one icon slot, which is not what the cache holds once an
        /// override has been applied.
        /// </summary>
        /// <remarks>
        /// RefreshRuntime.ApplyAchievementIconOverridesAsync loads the cached game, lets
        /// AchievementIconService write each override's materialized path onto it, and saves it
        /// back -- so the cached value for an overridden slot is the user's own file under
        /// icon_cache/&lt;game&gt;/custom. Read as a baseline it compares equal to the row's current
        /// path, which made every override read as "no override": the slot's text went blank, the
        /// whole-map write omitted the entry, and clearing the icon wrote the same custom path
        /// back, so nothing could be cleared.
        ///
        /// The provider's art is still on disk under its own name, so it is read from there. The
        /// retired compressed folder is probed second, as the icon previews do, for a game not
        /// refreshed since that mode was removed. A slot with no provider art at all resolves to
        /// null, which is what "the user's own outright" already means everywhere else.
        /// </remarks>
        private string ResolveProviderIconBaseline(
            string cachedPath,
            string fileStem,
            AchievementIconVariant variant)
        {
            if (!AchievementIconCachePathBuilder.IsCustomIconPath(cachedPath))
            {
                return cachedPath;
            }

            if (string.IsNullOrWhiteSpace(fileStem))
            {
                return null;
            }

            var disk = PlayniteAchievementsPlugin.Instance?.DiskImageService;
            if (disk == null)
            {
                return null;
            }

            var cached = disk.FindExistingAchievementIconCachePath(_gameIdText, fileStem, variant);
            if (!string.IsNullOrWhiteSpace(cached))
            {
                return cached;
            }

            var legacy = disk.GetLegacyCompressedAchievementIconCachePath(_gameIdText, fileStem, variant);
            return !string.IsNullOrWhiteSpace(legacy) && System.IO.File.Exists(legacy) ? legacy : null;
        }

        public void RefreshData()
        {
            using var perfScope = Common.PerfScope.Start(
                _logger,
                "Editor.RefreshData",
                thresholdMs: 10,
                context: "rows=" + AchievementRows.Count);

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

            // A running DispatcherTimer is rooted by the dispatcher and holds its handler, so it
            // would keep this view model - and through it every row - alive for the process.
            if (_undoStepTimer != null)
            {
                _undoStepTimer.Stop();
                _undoStepTimer.Tick -= UndoStepTimer_Tick;
                _undoStepTimer = null;
            }

            // The store outlives every window, so a missed unsubscribe here is the one leak this
            // history could introduce.
            _gameCustomDataStore.CustomDataWritten -= GameCustomDataStore_CustomDataWritten;
            _undoJournal.Clear();

            // The history is session-scoped, so the art it was holding for a redo goes with it.
            DiscardRetainedIconArt();

            // The rows outnumber everything else this view model holds, and each one points back
            // at it. Dropping them here means a window that is still rooted somewhere costs one
            // view model rather than a whole game's worth of rows and resolved art.
            foreach (var row in AchievementRows)
            {
                DetachRow(row);
            }

            SetSelectedRows(Array.Empty<AchievementEditorRow>());
            AchievementRows.Clear();
            _searchIndex.Clear();
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
            MarkUndoIntent(EditorEditIntent.Command("CustomProvider", "LOCPlayAch_Common_Label_Platform"));

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
            MarkUndoIntent(EditorEditIntent.Command("Add", "LOCPlayAch_Common_Add"));

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
            MarkUndoIntent(EditorEditIntent.Command("Duplicate", "LOCPlayAch_Common_Duplicate"));

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
            var duplicateUseSeparateLockedIcons = ResolveUseSeparateLockedIcons();
            foreach (var source in targets.OrderBy(row => AchievementRows.IndexOf(row)))
            {
                var row = source.CloneForDuplicate();
                AssignStableId(row);
                AttachRow(row, duplicateUseSeparateLockedIcons);
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
            using var perfScope = Common.PerfScope.Start(
                _logger,
                "Editor.SetCategoryForSelection",
                thresholdMs: 10,
                context: "rows=" + AchievementRows.Count);

            MarkUndoIntent(EditorEditIntent.Command("Category", "LOCPlayAch_Common_Label_Category"));

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
        /// Creates a top-level category and files the selected achievements in it. Returns the label
        /// in effect - the created one, or an existing one the name already belonged to - or null
        /// when the name was unusable.
        /// </summary>
        /// <remarks>
        /// The category is written into the order list first, so it exists even when nothing is
        /// selected to put in it: a category is otherwise only a label some achievement carries, and
        /// creating one to fill later would vanish on the next read. Nesting is not offered here -
        /// a created category is a root, and the Categories tab is where one is moved under another.
        /// </remarks>
        public string CreateAndAssignCategory(string leafName)
        {
            using var perfScope = Common.PerfScope.Start(
                _logger,
                "Editor.CreateAndAssignCategory",
                thresholdMs: 10,
                context: "rows=" + AchievementRows.Count);

            var label = CategoryPathHelper.SanitizeSegment(leafName);
            if (string.IsNullOrWhiteSpace(label))
            {
                return null;
            }

            try
            {
                var existing = AssignableCategoryOptions
                    .FirstOrDefault(option => CategoryPathHelper.IsSame(option, label));
                if (string.IsNullOrWhiteSpace(existing))
                {
                    // The whole known set, not just the stored order: a partial order would pin the
                    // new category ahead of categories that had never needed an entry of their own.
                    var order = AssignableCategoryOptions
                        .Where(option => !string.IsNullOrWhiteSpace(option))
                        .ToList();
                    order.Add(label);

                    // Per-game display state, scoped out of the library-wide passes like every
                    // other category order write.
                    _achievementOverridesService.SetAchievementCategoryMetadata(
                        _gameId,
                        order,
                        GameCustomDataLookup.GetAchievementCategoryImageOverrides(_gameId, _settings?.Persisted),
                        GameCustomDataLookup.GetGameSummaryCategory(_gameId, _settings?.Persisted),
                        affectsSummaryData: false);
                }
                else
                {
                    label = existing;
                }

                SetCategoryForSelection(label);

                // SetCategoryForSelection does nothing without a selection, and the refresh it would
                // have run is what puts a freshly created category into the picker.
                RefreshAssignmentState();
                return label;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed creating achievement category for gameId={_gameId}.");
                SetStatus(string.Format(L("LOCPlayAch_Status_Failed", "Error: {0}"), ex.Message), true);
                return null;
            }
        }

        /// <summary>
        /// Replaces the category types on every selected achievement with the set given.
        /// </summary>
        /// <remarks>
        /// A whole set rather than one type at a time, because the ticks describe a selection that
        /// may disagree. Merging into each row's own types meant a selection of a Base row and a
        /// Base+Update row could never be reduced to Base: ticking Base added what was already
        /// there and left Update behind on the second row. The edited set is taken literally and
        /// every selected row ends up with exactly it - which is also how the pane's other bulk
        /// fields behave.
        ///
        /// An empty set is the Default type, written as an override rather than left blank: blank
        /// would fall back to each provider's own type and the ticks would come straight back.
        /// </remarks>
        public void SetCategoryTypesForSelection(IEnumerable<string> categoryTypes)
        {
            using var perfScope = Common.PerfScope.Start(
                _logger,
                "Editor.SetCategoryTypesForSelection",
                thresholdMs: 10,
                context: "rows=" + AchievementRows.Count);

            MarkUndoIntent(EditorEditIntent.Command("CategoryType", "LOCPlayAch_Common_Label_Type"));

            var targets = ResolveSelectionTargets()
                .Where(row => row.CanEditAssignments && !string.IsNullOrWhiteSpace(row.OriginalApiName))
                .ToList();
            if (targets.Count == 0)
            {
                return;
            }

            var normalized = AchievementCategoryTypeHelper.NormalizeOrDefault(
                AchievementCategoryTypeHelper.Combine(
                    (categoryTypes ?? Enumerable.Empty<string>())
                        .Select(AchievementCategoryTypeHelper.Normalize)
                        .Where(categoryType => !string.IsNullOrWhiteSpace(categoryType))));

            StageAcross(targets, row => row.CategoryTypeValue = normalized);
            PersistCategoryAssignmentsFromRows();
            SyncBulkRowFromSelection();
        }

        /// <summary>
        /// Adds or removes one category type across the selection, merging into the types each row
        /// already carries. Prefer <see cref="SetCategoryTypesForSelection"/> for anything driven by
        /// a set of ticks; this stays for a caller that really does mean "toggle just this one".
        /// </summary>
        public void SetCategoryTypeForSelection(string categoryType, bool isSelected)
        {
            using var perfScope = Common.PerfScope.Start(
                _logger,
                "Editor.SetCategoryTypeForSelection",
                thresholdMs: 10,
                context: "rows=" + AchievementRows.Count);

            MarkUndoIntent(EditorEditIntent.Command("CategoryType", "LOCPlayAch_Common_Label_Type"));

            var targets = ResolveSelectionTargets()
                .Where(row => row.CanEditAssignments && !string.IsNullOrWhiteSpace(row.OriginalApiName))
                .ToList();
            var normalizedType = AchievementCategoryTypeHelper.Normalize(categoryType);
            if (targets.Count == 0 || string.IsNullOrWhiteSpace(normalizedType))
            {
                return;
            }

            // Merged into the type the row actually carries, not into its override alone. A row with
            // no override of its own has a null one, which reads as Default, so ticking a second
            // type used to drop the provider's grouping the ticks beside it were still showing.
            // The Categories tab and the shared row menu both toggle from the effective value.
            StageAcross(targets, row => row.CategoryTypeValue = AchievementCategoryTypeHelper.WithCategoryType(
                AchievementCategoryTypeHelper.NormalizeOrDefault(row.EffectiveCategoryTypeValue),
                normalizedType,
                isSelected));
            PersistCategoryAssignmentsFromRows();
            SyncBulkRowFromSelection();
        }

        /// <summary>
        /// Sets one achievement as a capstone at the given scope, or drops it.
        /// </summary>
        /// <remarks>
        /// A game-wide capstone stays single-selection: there is one game, so applying it across a
        /// selection would just leave whichever row happened to be written last. Category scope and
        /// clearing do apply across the selection, gated on the rows sitting in distinct
        /// categories, which is what stops them displacing each other as the write walks them.
        /// </remarks>
        public void SetCapstoneForSelection(bool isCapstone)
        {
            using var perfScope = Common.PerfScope.Start(
                _logger,
                "Editor.SetCapstoneForSelection",
                thresholdMs: 10,
                context: "rows=" + AchievementRows.Count);

            MarkUndoIntent(EditorEditIntent.Command("Capstone", "LOCPlayAch_Dynamic_Capstone"));

            var targets = ResolveSelectionTargets();
            if (targets.Count == 0)
            {
                return;
            }

            if (targets.Count == 1)
            {
                SetCapstoneForRow(targets[0], isCapstone);
                return;
            }

            if (!SelectionCategoriesAreDistinct())
            {
                return;
            }

            foreach (var target in targets)
            {
                SetCapstoneForRow(target, isCapstone);
            }

            SyncBulkRowFromSelection();
        }

        /// <summary>Whether the selection is one row whose capstone scope can be edited.</summary>
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
            using var perfScope = Common.PerfScope.Start(
                _logger,
                "Editor.SetGoalForSelection",
                thresholdMs: 10,
                context: "rows=" + AchievementRows.Count);

            MarkUndoIntent(EditorEditIntent.Command("Goal", "LOCPlayAch_ManageAchievements_Editor_Goal"));

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
            using var perfScope = Common.PerfScope.Start(
                _logger,
                "Editor.SetFilterScopeForSelection",
                thresholdMs: 10,
                context: "rows=" + AchievementRows.Count);

            MarkUndoIntent(EditorEditIntent.Command("FilterScope", "LOCPlayAch_Menu_Filters"));

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


            MarkUndoIntent(
                EditorEditIntent.Atomic("Revert", "LOCPlayAch_ManageAchievements_Editor_Revert"),
                targets.Select(row => row.OriginalApiName));
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

            MarkUndoIntent(EditorEditIntent.Atomic("ResetAll", "LOCPlayAch_Button_ResetAll"));

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

                // Reverting drops each reverted row's own capstone and leaves the rest of the set
                // alone, so reverting one achievement cannot clear a capstone elsewhere.
                foreach (var capstoneRow in targets.Where(row => row.IsCapstone))
                {
                    var capstoneApiName = NormalizeText(capstoneRow.OriginalApiName);
                    if (!string.IsNullOrWhiteSpace(capstoneApiName))
                    {
                        _achievementOverridesService.SetCapstone(_gameId, capstoneApiName, false);
                    }
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
        /// Drops the stored order for the whole game, so the list falls back to the order the
        /// providers hand over. Unlike reverting a selection there is nothing to re-seat: the
        /// positional list goes entirely.
        /// </summary>
        private const string PlatinumTrophyType = "platinum";

        private const string BaseCategoryType = "Base";

        /// <summary>The plugin's own mark, the one the notification preview shows.</summary>
        private const string BrandingIconPackUri =
            "pack://application:,,,/PlayniteAchievements;component/Resources/BrandingIcon.png";

        /// <summary>
        /// Makes the game's platinum trophy its capstone, authoring one when the game has no
        /// platinum of its own.
        /// </summary>
        private async Task ApplyAutoCapstoneAsync()
        {
            MarkUndoIntent(EditorEditIntent.Atomic("AutoCapstone", "LOCPlayAch_ManageAchievements_Custom_AutoCapstone"));

            try
            {
                // One capstone stands for one category, so the first thing to settle is which. A
                // game whose achievements all sit in one category has only one answer and is never
                // asked; anything else is a choice the user has to make, because adding a capstone
                // to a category means the game is not finished until that category is.
                if (!TryResolveAutoCapstoneCategory(out var category))
                {
                    return;
                }

                // An auto capstone this category already has is brought up to date rather than
                // joined by a second one.
                var existing = AchievementRows.FirstOrDefault(row =>
                    row?.IsAutoCapstone == true && IsInCategory(row, category));
                if (existing != null)
                {
                    ApplyAutoCapstoneDerivation(existing);
                    RefreshComputedState();
                    await SaveAsync().ConfigureAwait(true);
                    SetCapstoneForRow(existing, true);
                    SelectRowAndScrollTo(existing);
                    SetStatus(null, false);
                    return;
                }

                // Adopting a real platinum only makes sense for the game as a whole: a platinum is
                // never awarded for finishing one DLC, so a category capstone is always authored.
                var platinum = category == null ? ResolvePlatinumCapstoneRow() : null;
                if (platinum != null)
                {
                    SetCapstoneForRow(platinum, true);
                    SelectRowAndScrollTo(platinum);
                    SetStatus(null, false);
                    return;
                }

                await CreateCapstoneAchievementAsync(category).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed applying the automatic capstone for gameId={_gameId}.");
                SetStatus(string.Format(L("LOCPlayAch_Status_Failed", "Error: {0}"), ex.Message), true);
            }
        }

        /// <summary>Selects a row the editor picked, and asks the grid to show it.</summary>
        private void SelectRowAndScrollTo(AchievementEditorRow row)
        {
            SelectedRow = row;
            ScrollRowIntoViewRequested?.Invoke(this, row);
        }

        /// <summary>
        /// Which category the auto capstone should stand for. Null means the game as a whole, which
        /// is the answer whenever its achievements all sit in one category.
        /// </summary>
        /// <returns>False when the user dismissed the choice, so nothing should be written.</returns>
        private bool TryResolveAutoCapstoneCategory(out string category)
        {
            category = null;

            var categories = AchievementRows
                .Where(row => row != null && !string.IsNullOrWhiteSpace(row.OriginalApiName))
                .Select(row => AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(row.EffectiveCategoryLabel))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(label => label, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            // One category, or none to speak of: the capstone stands for the whole game, exactly as
            // it did before a game could hold more than one.
            if (categories.Count <= 1)
            {
                return true;
            }

            var chosen = PromptForAutoCapstoneCategory(categories);
            if (string.IsNullOrWhiteSpace(chosen))
            {
                return false;
            }

            category = chosen;
            return true;
        }

        /// <summary>
        /// Asks which category to stand for, listing the game's categories by their display label.
        /// </summary>
        private string PromptForAutoCapstoneCategory(IReadOnlyList<string> categories)
        {
            var options = categories
                .Select(label => new GenericItemOption(
                    AchievementCategoryTypeHelper.ToCategoryLabelDisplayText(label),
                    label))
                .ToList();

            var selected = API.Instance?.Dialogs?.ChooseItemWithSearch(
                options,
                _ => options,
                string.Empty,
                L("LOCPlayAch_Capstone_ChooseCategory", "Which category should this capstone stand for?"));

            // Description carries the raw label; the name is the localized display path.
            return selected?.Description;
        }

        private static bool IsInCategory(AchievementEditorRow row, string category)
        {
            if (category == null)
            {
                return true;
            }

            return string.Equals(
                AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(row.EffectiveCategoryLabel),
                AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(category),
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The platinum trophy that stands for finishing the game, or null when it has none.
        /// </summary>
        /// <remarks>
        /// Several platinums means DLC trophy sets alongside the base game's, and only the base
        /// game's marks the game complete. Providers type the group rather than leaving it to the
        /// label, so the base one is read off that type instead of guessed from the category text.
        /// When nothing is typed -- a game whose trophies were authored or came from a provider
        /// that does not group them -- the earliest in the achievement order wins, that order being
        /// what the list is showing.
        /// </remarks>
        private AchievementEditorRow ResolvePlatinumCapstoneRow()
        {
            var platinums = AchievementRows
                .Where(row => row != null &&
                              !string.IsNullOrWhiteSpace(row.OriginalApiName) &&
                              string.Equals(NormalizeText(row.TrophyType), PlatinumTrophyType, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (platinums.Count <= 1)
            {
                return platinums.FirstOrDefault();
            }

            var baseGame = platinums.FirstOrDefault(row =>
                AchievementCategoryTypeHelper.ParseValues(row.EffectiveCategoryTypeValue)
                    .Any(value => string.Equals(value, BaseCategoryType, StringComparison.OrdinalIgnoreCase)));
            return baseGame ?? platinums[0];
        }

        /// <summary>
        /// Authors the achievement that stands for finishing the game and makes it the capstone.
        /// </summary>
        /// <remarks>
        /// Its locked art is left unset on purpose: an achievement without one is drawn as a
        /// greyscale of its unlocked art already, which is what a locked platinum should look like,
        /// and storing a second copy of the same image would only be another file to keep in step.
        /// </remarks>
        private async Task CreateCapstoneAchievementAsync(string category)
        {
            var game = API.Instance?.Database?.Games?.Get(_gameId);
            var row = AchievementEditorRow.CreateNew(AchievementRows.Count + 1);
            AssignStableId(row);
            row.DisplayName = NormalizeText(game?.Name) ?? row.DisplayName;
            row.Description = L(
                "LOCPlayAch_ManageAchievements_Custom_AutoCapstoneDescription",
                "Obtain all Achievements.");
            row.TrophyType = PlatinumTrophyType;
            row.Hidden = false;
            row.IsAutoCapstone = true;

            // Derived here as well as on every refresh, so a game that is already finished gets a
            // capstone that is already unlocked -- and the first refresh after this sees no
            // crossing to announce.
            ApplyAutoCapstoneDerivation(row, category);

            // A source path, not a cached one: the save materializes it into this game's icon
            // cache the same way it does an icon dropped onto any other authored achievement.
            row.UnlockedIconPath = ResolveCapstoneIconSource(game);

            AttachRow(row);
            AchievementRows.Add(row);
            SelectRowAndScrollTo(row);
            RefreshComputedState();
            await SaveAsync().ConfigureAwait(true);

            if (string.IsNullOrWhiteSpace(row.OriginalApiName))
            {
                // The save refused it, and its own validation message says why.
                return;
            }

            SetCapstoneForRow(row, true);

            // A capstone sits in the category it stands for, which is what lets the resolver and
            // the maintainer both find it again from the row alone. A chosen category is filed
            // outright; without one the capstone falls back to wherever the achievements it stands
            // for are filed, when they agree on a single place.
            //
            // Only at authoring: unlike the rarity this is a starting point, not something kept in
            // step, so moving it afterwards sticks.
            var filing = category ?? AutoCapstoneCalculator
                .Derive(_gameDataSnapshotProvider?.GetHydratedGameData()?.Achievements?
                    .Where(achievement => !string.Equals(achievement?.ApiName, row.OriginalApiName, StringComparison.OrdinalIgnoreCase)))
                ?.Category;
            if (!string.IsNullOrWhiteSpace(filing))
            {
                row.CategoryLabel = filing;
                PersistCategoryAssignmentsFromRows();
            }

            // Only when the game already carries an order: pinning it otherwise would author one
            // for every achievement just to place this one.
            if (HasCustomOrder)
            {
                PersistCurrentOrder();
            }
        }

        /// <summary>
        /// Works the capstone's rarity and unlock out from the achievements it stands for, through
        /// the same rules the post-refresh maintenance uses so the two cannot drift apart.
        /// </summary>
        private void ApplyAutoCapstoneDerivation(AchievementEditorRow row, string category = null)
        {
            if (row == null)
            {
                return;
            }

            // The snapshot holds hydrated data until something drops it, and a category the user
            // changed in this window is exactly what decides the scope. Without this the capstone
            // would be worked out from the grouping as it stood before that edit.
            _gameDataSnapshotProvider?.Invalidate();

            var apiName = NormalizeText(row.OriginalApiName);
            // The row's own category when it has one, so an existing auto capstone keeps standing
            // for the category it was filed in rather than silently widening to the whole game.
            var scope = category ?? NormalizeText(row.EffectiveCategoryLabel);
            var derived = AutoCapstoneCalculator.Derive(
                _gameDataSnapshotProvider?.GetHydratedGameData()?.Achievements?
                    .Where(achievement => string.IsNullOrWhiteSpace(apiName) ||
                                          !string.Equals(achievement?.ApiName, apiName, StringComparison.OrdinalIgnoreCase)),
                scope);
            if (derived == null)
            {
                return;
            }

            row.SetRarityFromSource(derived.GlobalPercentUnlocked, derived.Rarity);
            row.SetUnlockedFromSource(derived.Unlocked);
            row.UnlockTime = derived.Unlocked ? derived.UnlockTimeUtc : null;
        }

        /// <summary>
        /// The image to stand the capstone on: the game's own icon, then its cover, then the
        /// plugin's mark, so it is never left without one.
        /// </summary>
        private string ResolveCapstoneIconSource(Playnite.SDK.Models.Game game)
        {
            var icon = ResolvePlayniteAssetFile(game?.Icon);
            if (!string.IsNullOrWhiteSpace(icon))
            {
                return icon;
            }

            var cover = ResolvePlayniteAssetFile(game?.CoverImage);
            return !string.IsNullOrWhiteSpace(cover) ? cover : ResolveBrandingIconFile();
        }

        private static string ResolvePlayniteAssetFile(string databasePath)
        {
            var normalized = NormalizeText(databasePath);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            var full = API.Instance?.Database?.GetFullFilePath(normalized);
            return !string.IsNullOrWhiteSpace(full) && File.Exists(full) ? full : null;
        }

        /// <summary>
        /// Unpacks the plugin's mark to a file, because an achievement's icon is stored as a path
        /// and the mark ships inside the assembly.
        /// </summary>
        private string ResolveBrandingIconFile()
        {
            try
            {
                var target = Path.Combine(Path.GetTempPath(), "playniteachievements-capstone.png");
                if (File.Exists(target))
                {
                    return target;
                }

                var resource = System.Windows.Application.GetResourceStream(new Uri(BrandingIconPackUri));
                if (resource?.Stream == null)
                {
                    return null;
                }

                using (var source = resource.Stream)
                using (var file = File.Create(target))
                {
                    source.CopyTo(file);
                }

                return target;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed unpacking the branding icon for the automatic capstone.");
                return null;
            }
        }

        private void ResetOrder()
        {
            using var perfScope = Common.PerfScope.Start(
                _logger,
                "Editor.ResetOrder",
                thresholdMs: 10,
                context: "rows=" + AchievementRows.Count);

            MarkUndoIntent(EditorEditIntent.Command("ResetOrder", "LOCPlayAch_ManageAchievements_Order_Reset"));

            try
            {
                _achievementOverridesService.SetAchievementOrderOverride(_gameId, Array.Empty<string>());
                HasCustomOrder = false;
                RaiseAssignmentsChanged();
                ReloadData();
                SetStatus(null, false);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed resetting achievement order for gameId={_gameId}.");
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
            MarkUndoIntent(EditorEditIntent.Command("Delete", "LOCPlayAch_Button_Delete"));

            var targets = ResolveSelectionTargets()
                .Where(row => !row.IsProviderRow)
                .ToList();
            if (targets.Count == 0)
            {
                return;
            }

            foreach (var row in targets)
            {
                DetachRow(row);
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

            MarkUndoIntent(EditorEditIntent.Atomic("Import", "LOCPlayAch_Common_Import"));

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

            var importUseSeparateLockedIcons = ResolveUseSeparateLockedIcons();
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
                AttachRow(row, importUseSeparateLockedIcons);
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
            using var perfScope = Common.PerfScope.Start(
                _logger,
                "Editor.Save",
                thresholdMs: 10,
                context: "rows=" + AchievementRows.Count);

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

                // Read before the unlocked slot is materialized, while the two are still in the
                // same terms: the question is whether the locked slot is art of its own or a copy
                // of the unlocked one, and a row seeded from a projection carries a copy whenever
                // the game has separate locked icons off.
                var hasOwnLockedArt = AchievementIconResolver.HasExplicitLockedIcon(
                    definition.LockedIconPath,
                    definition.UnlockedIconPath);

                definition.UnlockedIconPath = await MaterializeIconSourceAsync(
                    definition.UnlockedIconPath,
                    fileStem,
                    AchievementIconVariant.Unlocked,
                    errors).ConfigureAwait(true);

                // A copy is not materialized. Doing so minted a real locked file out of the
                // unlocked art, which every reader then takes for a locked icon somebody chose --
                // pinning that art in full colour for good, and for this achievement only, since
                // nothing afterwards can tell it from a locked icon that was authored.
                definition.LockedIconPath = hasOwnLockedArt
                    ? await MaterializeIconSourceAsync(
                        definition.LockedIconPath,
                        fileStem,
                        AchievementIconVariant.Locked,
                        errors).ConfigureAwait(true)
                    : null;
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
            using var replaceScope = Common.PerfScope.Start(_logger, "Editor.ReplaceRows", thresholdMs: 5);
            var previousSelectedId = SelectedRow?.NormalizedId;
            var previousSelectedApiNames = _selectedRows
                .Where(row => !string.IsNullOrWhiteSpace(row?.OriginalApiName))
                .Select(row => row.OriginalApiName)
                .ToList();
            foreach (var row in AchievementRows)
            {
                DetachRow(row);
            }

            // The multi-selection is made of the rows being replaced, and the grid only echoes a
            // fresh one back once it has processed the reset. Dropping it here keeps a selection
            // edit that lands in between from staging onto detached rows, where it would be
            // written out of a collection that no longer contains them.
            SetSelectedRows(Array.Empty<AchievementEditorRow>());

            AchievementRows.Clear();
            var useSeparateLockedIcons = ResolveUseSeparateLockedIcons();
            foreach (var row in rows ?? Enumerable.Empty<AchievementEditorRow>())
            {
                AttachRow(row, useSeparateLockedIcons);
                AchievementRows.Add(row);
            }

            // Keep the user's place: a save or reload rebuilds the rows, and the details pane
            // should stay on the achievement they were editing.
            SelectedRow = AchievementRows.FirstOrDefault(row =>
                              !string.IsNullOrWhiteSpace(previousSelectedId) &&
                              string.Equals(row?.NormalizedId, previousSelectedId, StringComparison.OrdinalIgnoreCase))
                          ?? AchievementRows.FirstOrDefault();

            // SelectedRow restores one row, so a multi-row selection would otherwise come back as
            // whichever row the pane had been editing. A reload the editor did not ask for should
            // not cost the user their selection.
            if (previousSelectedApiNames.Count > 1)
            {
                RestoreSelectionRequested?.Invoke(this, previousSelectedApiNames);
            }
        }

        /// <summary>
        /// Rereads the per-achievement category, type, and capstone assignments for saved rows.
        /// These live in the game's custom data, edited by the Category and Capstones tabs too,
        /// so rows show the current state rather than anything staged for Save.
        /// </summary>
        /// <param name="resolved">
        /// A record the caller has already resolved. Resolving clones the game's whole
        /// customization, so a caller holding one passes it rather than paying for a second.
        /// </param>
        private void RefreshAssignmentState(ResolvedGameCustomData resolved = null)
        {
            _isRefreshingAssignments = true;
            try
            {
                // One resolve for all three: this runs after every save, and each lookup helper
                // would otherwise clone the game's whole record again.
                resolved = resolved ?? ResolveCurrentCustomData();
                var categoryOverrides = GetCurrentCategoryOverrideMap(resolved);
                var categoryTypeOverrides = GetCurrentCategoryTypeOverrideMap(resolved);
                var capstones = BuildCapstoneState(resolved);

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
                        row.SetCapstoneStateFromSource(false, null, null, null);
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
                    ApplyCapstoneStateToRow(row, apiName, capstones);
                }

                RefreshAssignableCategoryOptions(resolved);
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

        /// <summary>
        /// Rebuilds the categories the pickers offer: every label the rows carry, plus labels that
        /// only hold user state - the order, the art overrides, the summary pick - in tree order.
        /// </summary>
        /// <remarks>
        /// Reads the rows rather than the hydrated snapshot. A row's effective label is already the
        /// override where there is one and the provider's label otherwise, so the snapshot pass
        /// restated what the rows say - and, since every assignment invalidates that snapshot, it
        /// paid for a cold rebuild of it on each edit. The resolved record is passed in for the
        /// same reason: each lookup helper clones the game's whole customization.
        /// </remarks>
        private void RefreshAssignableCategoryOptions(ResolvedGameCustomData resolved)
        {
            // An empty grid means a load that failed or has not finished, not a game with no
            // categories: synchronising to empty here blanked the picker under the user.
            if (AchievementRows.Count == 0)
            {
                return;
            }

            var categoryOrder = resolved?.AchievementCategoryOrder;
            var labels = new List<string>(AchievementRows.Count);
            labels.AddRange(AchievementRows
                .Select(row => row?.EffectiveCategoryLabel)
                .Where(label => !string.IsNullOrWhiteSpace(label)));
            labels.AddRange(resolved?.AchievementCategoryImageOverrides?.Keys ?? Enumerable.Empty<string>());
            labels.AddRange(categoryOrder ?? new List<string>());
            var summaryCategory = resolved?.GameSummaryCategory;
            if (!string.IsNullOrWhiteSpace(summaryCategory?.Label))
            {
                labels.Add(summaryCategory.Label);
            }

            var ordered = AchievementCategoryFilterOrderHelper.BuildOrderedCategoryTree(
                labels.Where(label => !string.IsNullOrWhiteSpace(label)),
                categoryOrder);

            // The pickers rebuild their option rows once per collection event, so an unchanged
            // label set has to raise none.
            if (AssignableCategoryOptions.SequenceEqual(ordered, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

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
                    AchievementCategoryTypeHelper.ParseValues(EditTarget?.EffectiveCategoryTypeValue),
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

            // The whole tick state, not the one that changed: the set the user has built is what
            // every selected row takes.
            SetCategoryTypesForSelection(TypeSelectionOptions
                .Where(candidate => candidate?.IsSelected == true)
                .Select(candidate => candidate.Value));
        }

        private void PersistAssignmentMaps(
            IReadOnlyDictionary<string, string> categoryOverrides,
            IReadOnlyDictionary<string, string> categoryTypeOverrides)
        {
            try
            {
                _achievementOverridesService.SetAchievementCategoryOverrides(_gameId, categoryOverrides, categoryTypeOverrides);
                RefreshAssignmentState();

                // Through the debounce, not raised directly: only its flush marks the notification
                // as this editor's own, and without that mark the cascade came back as a refresh
                // request that reloaded every row - clearing the grid's selection on the way - for
                // an edit the editor was already showing.
                RaiseAssignmentsChanged();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed saving custom achievement category assignments for gameId={_gameId}.");
                SetStatus(string.Format(L("LOCPlayAch_Status_Failed", "Error: {0}"), ex.Message), true);
            }
        }

        /// <summary>
        /// The game's capstones resolved once per refresh, alongside the achievements they were
        /// resolved against so a row can be told what its own category ends up standing on.
        /// </summary>
        private struct CapstoneState
        {
            public CapstoneState(
                CapstoneResolver resolver,
                Dictionary<string, AchievementDetail> byApiName,
                bool materialized)
            {
                Resolver = resolver;
                ByApiName = byApiName;
                Materialized = materialized;
                CategoryDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            public CapstoneResolver Resolver { get; }

            public Dictionary<string, AchievementDetail> ByApiName { get; }

            public bool Materialized { get; }

            /// <summary>
            /// Memo for the category display text, which normalizes a path and reads a localized
            /// string. Rows share categories heavily, so this is computed once per category rather
            /// than once per row.
            /// </summary>
            public Dictionary<string, string> CategoryDisplayNames { get; }
        }

        private CapstoneState BuildCapstoneState(ResolvedGameCustomData resolved)
        {
            var achievements = _gameDataSnapshotProvider?.GetHydratedGameData()?.Achievements
                ?? new List<AchievementDetail>();
            var materialized = resolved?.CapstonesMaterialized == true;

            // Indexed once per refresh rather than scanned per row: this runs for every row in the
            // grid, and a linear lookup inside it makes re-seeding quadratic in the achievement
            // count, which a large game feels as a hang on the click that triggered it.
            var byApiName = new Dictionary<string, AchievementDetail>(StringComparer.OrdinalIgnoreCase);
            foreach (var achievement in achievements)
            {
                var key = (achievement?.ApiName ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(key) && !byApiName.ContainsKey(key))
                {
                    byApiName[key] = achievement;
                }
            }

            return new CapstoneState(
                CapstoneResolver.Resolve(achievements, resolved?.Capstones, materialized),
                byApiName,
                materialized);
        }

        private static void ApplyCapstoneStateToRow(
            AchievementEditorRow row,
            string apiName,
            CapstoneState capstones)
        {
            var resolver = capstones.Resolver;
            var isCapstone = resolver.IsCapstone(apiName);

            // What this row's own category stands on, which is not always this row: a category with
            // no capstone of its own shows the one it inherits from an ancestor.
            var category = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(
                FindAchievement(capstones, apiName)?.Category);
            var categoryApiName = resolver.ResolveForCategory(category);

            // A game holding exactly one capstone has that capstone standing for the whole of it,
            // which is worth saying plainly. With several there is no single one to name.
            var gameApiName = resolver.Count == 1 ? resolver.EffectiveApiNames.First() : null;

            // Naming the same achievement on both lines is noise, so the category line yields.
            if (string.Equals(categoryApiName, gameApiName, StringComparison.OrdinalIgnoreCase))
            {
                categoryApiName = null;
            }

            row.SetCapstoneStateFromSource(
                isCapstone,
                ResolveCategoryDisplayName(capstones, category),
                ResolveDisplayName(capstones, categoryApiName),
                ResolveDisplayName(capstones, gameApiName));

            // Only worth resolving when the button would actually say Replace.
            row.CapstoneReplacesDisplayName = isCapstone || !resolver.HasOwnCapstone(category)
                ? null
                : ResolveDisplayName(capstones, resolver.ResolveForCategory(category));
        }

        private static string ResolveCategoryDisplayName(CapstoneState capstones, string category)
        {
            var key = category ?? string.Empty;
            if (!capstones.CategoryDisplayNames.TryGetValue(key, out var display))
            {
                display = AchievementCategoryTypeHelper.ToCategoryLabelDisplayText(category);
                capstones.CategoryDisplayNames[key] = display;
            }

            return display;
        }

        private static string ResolveDisplayName(CapstoneState capstones, string apiName)
        {
            var match = FindAchievement(capstones, apiName);
            return match == null ? null : (match.DisplayName ?? match.ApiName);
        }

        private static AchievementDetail FindAchievement(CapstoneState capstones, string apiName)
        {
            if (string.IsNullOrWhiteSpace(apiName) || capstones.ByApiName == null)
            {
                return null;
            }

            capstones.ByApiName.TryGetValue(apiName.Trim(), out var match);
            return match;
        }

        private void SetCapstoneForRow(AchievementEditorRow row, bool isCapstone)
        {
            var apiName = NormalizeText(row?.OriginalApiName);
            if (string.IsNullOrWhiteSpace(apiName))
            {
                RefreshAssignmentState();
                return;
            }

            try
            {
                _achievementOverridesService.SetCapstone(_gameId, apiName, isCapstone);

                // Re-seeded from the store, so the rows are right whatever the snapshot holds. The
                // snapshot is only read here for categories and names, which a capstone write does
                // not move, so this deliberately runs before the invalidation below and pays for no
                // re-hydration of its own.
                RefreshAssignmentState();

                // The snapshot caches hydrated data until something drops it, and the host's own
                // invalidation is debounced. A reload landing inside that window would rebuild
                // these rows from pre-write data and put the capstone flag back as it was, which
                // is what left the status glyph stale on some clicks and not others.
                _gameDataSnapshotProvider?.Invalidate();

                CapstoneChanged?.Invoke(
                    this,
                    new CapstoneChangedEventArgs(
                        isCapstone ? apiName : null,
                        isCapstone ? row.DisplayName : null));
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

        /// <summary>
        /// Counterpart to <see cref="AttachRow"/>: drops the row's subscriptions and its search
        /// text. The index is keyed by row reference and only a rebuild purges it, so a row that
        /// leaves the collection without one would be retained by it.
        /// </summary>
        private void DetachRow(AchievementEditorRow row)
        {
            if (row == null)
            {
                return;
            }

            row.PropertyChanged -= Row_PropertyChanged;
            row.RevealStateChanged -= Row_RevealStateChanged;
            row.ValueChanging = null;
            _searchIndex.Invalidate(row);
        }

        private bool ResolveUseSeparateLockedIcons()
        {
            return GameCustomDataLookup.ShouldUseSeparateLockedIcons(_gameId, _settings?.Persisted);
        }

        /// <param name="useSeparateLockedIcons">
        /// Pre-resolved by callers that attach more than one row. Resolving it builds an entire
        /// ResolvedGameCustomData -- a deep clone of this game's record plus a dozen collection
        /// rebuilds, taking the store's lock -- to read a single bool, so doing it per row
        /// multiplied the achievement count by the game's customization size every time the
        /// editor opened. Null keeps the original inline resolve for single-row callers.
        /// </param>
        private void AttachRow(AchievementEditorRow row, bool? useSeparateLockedIcons = null)
        {
            if (row == null)
            {
                return;
            }

            // Icon masking follows the same display settings as the achievement grids, so a
            // locked or hidden custom row masks its icon until clicked.
            row.ShowHiddenIcon = _settings?.Persisted?.ShowHiddenIcon ?? false;
            row.ShowLockedIcon = _settings?.Persisted?.ShowLockedIcon ?? true;
            row.ShowHiddenTitle = _settings?.Persisted?.ShowHiddenTitle ?? false;
            row.ShowHiddenDescription = _settings?.Persisted?.ShowHiddenDescription ?? false;
            row.ShowLockedTitle = _settings?.Persisted?.ShowLockedTitle ?? true;
            row.ShowLockedDescription = _settings?.Persisted?.ShowLockedDescription ?? true;
            row.ShowHiddenTrophy = _settings?.Persisted?.ShowHiddenTrophy ?? true;
            row.ShowLockedTrophy = _settings?.Persisted?.ShowLockedTrophy ?? true;
            row.ShowHiddenPoints = _settings?.Persisted?.ShowHiddenPoints ?? true;
            row.ShowLockedPoints = _settings?.Persisted?.ShowLockedPoints ?? true;
            row.UseSeparateLockedIcons = useSeparateLockedIcons
                ?? ResolveUseSeparateLockedIcons();
            row.ConfigureIconPathDisplay(
                path => _managedCustomIconService?.GetManagedDisplayPath(path, _gameIdText) ?? path,
                text => _managedCustomIconService?.ResolveManagedDisplayPath(text, _gameIdText) ?? text);
            row.PropertyChanged -= Row_PropertyChanged;
            row.PropertyChanged += Row_PropertyChanged;
            row.RevealStateChanged -= Row_RevealStateChanged;
            row.RevealStateChanged += Row_RevealStateChanged;

            // The value a field is about to replace only exists inside the setter, so the history
            // is told from there rather than reconstructing it afterwards.
            row.ValueChanging = RecordRowValueChange;
        }

        private void Row_RevealStateChanged(object sender, EventArgs e)
        {
            if (!_isTogglingReveal)
            {
                RefreshRevealHeaderState();
            }
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

            // Reveal state never persists: it only decides what the grid is currently masking.
            // The column headers follow it through RevealStateChanged, which fires once per change
            // rather than once per property.
            if (IsRevealStateProperty(e.PropertyName))
            {
                return;
            }

            // Only for a property that is actually stored. An edit also raises derived ones, and
            // naming a gesture after those closed the real step and opened another that the
            // write which followed landed in.
            if (PersistedRowProperties.Contains(e.PropertyName ?? string.Empty))
            {
                MarkUndoIntent(
                    EditorEditIntent.FieldEdit(ResolveFieldGesture(e.PropertyName), "LOCPlayAch_Common_Edit"));
            }

            // A trophy grade or point value the user just entered stays on screen. Masking it the
            // moment it is typed hides their own edit, and on a game that had no trophies or points
            // at all every value they add would appear as a placeholder. This only runs for an
            // attached row, so a load never trips it: rows are populated before AttachRow
            // subscribes. Reveal state is per row and never persists.
            if (sender is AchievementEditorRow valueEditedRow)
            {
                if (e.PropertyName == nameof(AchievementEditorRow.TrophyType) && valueEditedRow.HasTrophyType)
                {
                    valueEditedRow.IsTrophyRevealed = true;
                }
                else if (e.PropertyName == nameof(AchievementEditorRow.PointsText) && valueEditedRow.HasPoints)
                {
                    valueEditedRow.IsPointsRevealed = true;
                }
            }

            if (e.PropertyName == nameof(AchievementEditorRow.IsCapstone))
            {
                // The row re-seeds itself from the store after every write, and that re-seed sets
                // IsCapstone too; persisting it again would write the same edit twice.
                if (!_isRefreshingAssignments &&
                    sender is AchievementEditorRow capstoneRow &&
                    !capstoneRow.SuppressCapstonePersist)
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
                    // Without the provider baseline every row reads as not hidden, so unhiding a
                    // provider-hidden achievement would store nothing and snap straight back.
                    // Storing the chosen value keeps a record that happens to agree with the
                    // provider, which costs a row and holds the edit.
                    case nameof(AchievementEditorRow.Hidden):
                        WriteProviderField(
                            apiName,
                            AchievementEditableField.Hidden,
                            _providerBaselinesResolved && row.Hidden == row.ProviderHidden
                                ? (bool?)null
                                : row.Hidden);
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
            using var perfScope = Common.PerfScope.Start(
                _logger,
                "Editor.MoveItemsByApiName",
                thresholdMs: 10,
                context: "rows=" + AchievementRows.Count);

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
            using var perfScope = Common.PerfScope.Start(
                _logger,
                "Editor.MoveItemsToEndByApiName",
                thresholdMs: 10,
                context: "rows=" + AchievementRows.Count);

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
            // Marked here rather than at the order write, which the add flow also goes through
            // under its own gesture.
            MarkUndoIntent(EditorEditIntent.Command(
                "Reorder",
                "LOCPlayAch_Common_Reorder"));

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
                HasCustomOrder = ordered.Count > 0;
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

            // A bulk edit writes once per selected row; naming the gesture here keeps all of
            // those writes in one undo step.
            MarkUndoIntent(EditorEditIntent.FieldEdit(ResolveFieldGesture(property), "LOCPlayAch_Common_Edit"));

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
            MarkUndoIntent(
                EditorEditIntent.Command("Icon:" + variant, "LOCPlayAch_Column_Icon"),
                targets.Select(row => row.OriginalApiName));
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

        /// <summary>
        /// Writes the category and type assignments as one pair of maps, keeping every value the
        /// user picked.
        /// </summary>
        /// <remarks>
        /// An assignment is no longer dropped for matching the provider's own label. The provider
        /// baseline is not trustworthy enough to decide that: it is not stored as a field of its
        /// own, so it is reconstructed from the cached category, which a backfill can fill with a
        /// value that started life as a user assignment. Once that happened, the category the user
        /// picked looked redundant and was discarded - and, the map being written whole, their rows
        /// kept whichever older assignment they already had, with no way to move them at all.
        ///
        /// Writing a redundant entry costs one string per achievement and is undone by clearing the
        /// assignment, which still removes the override because a blank value is skipped below.
        /// </remarks>
        private void PersistCategoryAssignmentsFromRows()
        {
            PersistAssignmentMaps(
                BuildAssignmentMap(row => row.CategoryLabel),
                BuildAssignmentMap(row => row.CategoryTypeValue));
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
        private void PersistIconOverridesFromRows(IReadOnlyList<AchievementEditorRow> changedRows = null)
        {
            // The maps are written whole, so rebuilding them without the provider baseline would
            // both stamp every provider icon in as an override and drop the real ones already
            // stored. Refusing the write leaves the stored icons alone.
            if (!_providerBaselinesResolved)
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
                RaiseIconOverridesSaved(changedRows);
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
            // Both the set and the clear count as moved: clearing has to carry the provider's own
            // art back into the cached rows, which nothing else would do.
            var touched = new List<AchievementEditorRow>();
            foreach (var row in rows ?? Array.Empty<AchievementEditorRow>())
            {
                if (row == null || !row.IsProviderRow)
                {
                    continue;
                }

                var current = NormalizeText(ReadIcon(row, variant));

                // Blank is not the only way a row says "no override": holding the provider's own
                // path says it too, and that is the form an undo restores and a clear leaves
                // behind. Reading only the blank case made both look like a fresh custom icon, so
                // undoing one materialized the provider's art into the managed folder as an
                // override rather than removing it. Same test as IsIconOverride, which is the
                // definition the display text and the override write already use.
                var isOverride = !string.IsNullOrWhiteSpace(current) &&
                    !string.Equals(
                        current,
                        NormalizeText(ReadProviderIcon(row, variant)),
                        StringComparison.OrdinalIgnoreCase);

                if (!isOverride)
                {
                    // The locked slot clears to blank rather than to the provider's path. Blank is
                    // how "no locked icon of its own" is said everywhere else, and the locked look
                    // is then derived from whatever the unlocked slot holds -- so clearing a locked
                    // icon over a custom unlocked one grays that custom icon, instead of putting
                    // the provider's original back in full colour. The unlocked slot has nothing to
                    // derive from, so it still carries the provider's own art back.
                    StageAcross(new[] { row }, target => WriteIcon(
                        target,
                        variant,
                        variant == AchievementIconVariant.Locked ? null : ReadProviderIcon(target, variant)));
                    touched.Add(row);
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

                touched.Add(row);
            }

            if (errors.Count > 0)
            {
                SetStatus(string.Join(Environment.NewLine, errors.Take(8)), true);
            }

            PersistIconOverridesFromRows(touched);
        }

        /// <summary>
        /// Announces the provider rows whose icon this edit moved. Authored rows keep their icons on
        /// their own definition and reach the cache through the save, so they are left out.
        /// </summary>
        private void RaiseIconOverridesSaved(IReadOnlyList<AchievementEditorRow> changedRows)
        {
            if (changedRows == null || changedRows.Count == 0)
            {
                return;
            }

            var apiNames = changedRows
                .Where(row => row != null && row.IsProviderRow)
                .Select(row => NormalizeText(row.OriginalApiName))
                .Where(apiName => !string.IsNullOrWhiteSpace(apiName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (apiNames.Count == 0)
            {
                return;
            }

            IconOverridesSaved?.Invoke(this, new IconOverridesSavedEventArgs(apiNames));
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
        /// <summary>
        /// Every non-blank assignment the rows carry, keyed by ApiName. A blank one is left out,
        /// which is what removes the override for a row whose assignment was cleared.
        /// </summary>
        private Dictionary<string, string> BuildAssignmentMap(Func<AchievementEditorRow, string> selector)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in AchievementRows)
            {
                var apiName = row?.OriginalApiName;
                var value = selector(row);
                if (string.IsNullOrWhiteSpace(apiName) || string.IsNullOrWhiteSpace(value))
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
            _selectedCategoryFilters.Count > 0 ||
            _selectedTypeFilters.Count > 0 ||
            _selectedCustomizationFilters.Count > 0;

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

            if (_selectedCategoryFilters.Count > 0 &&
                !_selectedCategoryFilters.Contains(row.EffectiveCategoryLabel ?? string.Empty))
            {
                return false;
            }

            // A row carries several category types at once, so it passes when any of its own is
            // ticked rather than when its whole joined value matches one. Read through the shared
            // component cache and walked by index: this runs for every row on every refresh, and
            // parsing the value afresh each time made the type filter the costliest of the three.
            if (_selectedTypeFilters.Count > 0 && !MatchesSelectedTypes(row))
            {
                return false;
            }

            if (_selectedCustomizationFilters.Count > 0 &&
                !_selectedCustomizationFilters.Contains(
                    row.IsCustomized ? CustomizedFilterKey : NotCustomizedFilterKey))
            {
                return false;
            }

            return true;
        }

        private bool MatchesSelectedTypes(AchievementEditorRow row)
        {
            var components = AchievementCategoryTypeHelper.GetCanonicalComponents(
                row.EffectiveCategoryTypeValue);
            for (var i = 0; i < components.Count; i++)
            {
                if (_selectedTypeFilters.Contains(components[i]))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Option keys for the customization filter. Stored rather than displayed: the shared
        /// multi-select model keys its options by string, and the label is resolved separately so
        /// it can be localized.
        /// </summary>
        private const string CustomizedFilterKey = "Customized";

        private const string NotCustomizedFilterKey = "NotCustomized";

        private void RebuildSearchIndex()
        {
            _searchIndex.Rebuild(AchievementRows);
        }


        /// <summary>
        /// Narrows the grid to the ticked categories. Nothing ticked restricts nothing, so the
        /// button reads as the facet's own name rather than as an "all" that has to be chosen.
        /// </summary>
        /// <remarks>
        /// The same model the overview grids use, so these read and behave like the filters
        /// everywhere else: a summary button over a checkable menu, several choices at once, and
        /// -- for the categories -- the tree connectors the category grid draws.
        /// </remarks>
        public GridMultiSelectFilter CategoryFilter { get; }

        public GridMultiSelectFilter TypeFilter { get; }

        public GridMultiSelectFilter CustomizationFilter { get; }

        /// <summary>
        /// Builds the three filter drop-downs. Each reads its own options live, so a rebuild only
        /// has to raise the change rather than refill a collection.
        /// </summary>
        private GridMultiSelectFilter BuildCategoryFilter()
        {
            return new GridMultiSelectFilter(
                this,
                nameof(FilterOptionsChanged),
                () => GetSelectedFilterText(
                    _selectedCategoryFilters,
                    _categoryFilterOptions,
                    ResourceProvider.GetString("LOCPlayAch_Common_Label_Category")),
                () => _categoryFilterOptions,
                option => _selectedCategoryFilters.Contains(option),
                (option, isSelected) => ToggleFilter(_selectedCategoryFilters, option, isSelected),
                getDisplayLabel: CategoryPathHelper.GetLeafName)
            {
                RendersCategoryTree = true,
                MinWidth = 140
            };
        }

        private GridMultiSelectFilter BuildTypeFilter()
        {
            return new GridMultiSelectFilter(
                this,
                nameof(FilterOptionsChanged),
                () => GetSelectedFilterText(
                    _selectedTypeFilters,
                    AchievementCategoryTypeHelper.AssignableCategoryTypes,
                    ResourceProvider.GetString("LOCPlayAch_Common_Label_Type"),
                    ManageAchievementsCategoryViewModel.GetCategoryTypeDisplayName),
                () => AchievementCategoryTypeHelper.AssignableCategoryTypes,
                option => _selectedTypeFilters.Contains(option),
                (option, isSelected) => ToggleFilter(_selectedTypeFilters, option, isSelected),
                getDisplayLabel: ManageAchievementsCategoryViewModel.GetCategoryTypeDisplayName)
            {
                MinWidth = 140
            };
        }

        private GridMultiSelectFilter BuildCustomizationFilter()
        {
            var options = new[] { CustomizedFilterKey, NotCustomizedFilterKey };
            return new GridMultiSelectFilter(
                this,
                nameof(FilterOptionsChanged),
                () => GetSelectedFilterText(
                    _selectedCustomizationFilters,
                    options,
                    ResourceProvider.GetString("LOCPlayAch_Filter_CustomizationSelectorPlaceholder"),
                    GetCustomizationFilterLabel),
                () => options,
                option => _selectedCustomizationFilters.Contains(option),
                (option, isSelected) => ToggleFilter(_selectedCustomizationFilters, option, isSelected),
                getDisplayLabel: GetCustomizationFilterLabel,
                // Two options that are always meaningful, so this one never auto-hides the way a
                // filter built from whatever the game happens to carry does.
                hasAvailableAction: () => true)
            {
                MinWidth = 140
            };
        }

        private static string GetCustomizationFilterLabel(string option)
        {
            return string.Equals(option, CustomizedFilterKey, StringComparison.OrdinalIgnoreCase)
                ? ResourceProvider.GetString("LOCPlayAch_Tagging_Customized")
                : ResourceProvider.GetString("LOCPlayAch_Tagging_NotCustomized");
        }

        private void ToggleFilter(HashSet<string> selection, string option, bool isSelected)
        {
            if (string.IsNullOrWhiteSpace(option))
            {
                return;
            }

            if (isSelected)
            {
                selection.Add(option);
            }
            else
            {
                selection.Remove(option);
            }

            NotifyFilterChanged();
        }

        /// <summary>
        /// The ticked options joined for the button face, or the facet's own name when none are.
        /// </summary>
        private static string GetSelectedFilterText(
            HashSet<string> selectedValues,
            IEnumerable<string> options,
            string placeholder,
            Func<string, string> displayText = null)
        {
            if (selectedValues == null || selectedValues.Count == 0)
            {
                return placeholder;
            }

            var ordered = (options ?? Enumerable.Empty<string>())
                .Where(option => !string.IsNullOrWhiteSpace(option) && selectedValues.Contains(option))
                .ToList();
            if (ordered.Count == 0)
            {
                ordered.AddRange(selectedValues.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
            }

            return string.Join(", ", ordered.Select(value => displayText?.Invoke(value) ?? value));
        }

        /// <summary>
        /// Raised when the option sets behind the filters change, which is what the drop-downs
        /// subscribe to rather than each holding a collection of its own.
        /// </summary>
        public object FilterOptionsChanged => null;

        /// <summary>
        /// This game's categories as the tree the assignment pickers draw, for the row context
        /// menu's category submenu. Built on demand: it is read once when that menu opens.
        /// </summary>
        public IReadOnlyList<CategoryPickerOption> AssignableCategoryPickerOptions =>
            CategoryPickerResolver.BuildOptions(
                AssignableCategoryOptions.ToList(),
                AssignableCategoryOptions.ToList(),
                synthesizedAreSelectable: false);

        private void NotifyFilterChanged()
        {
            OnPropertyChanged(nameof(IsFiltering));
            // The header toggles summarize the rows on screen, and the filter decides which those
            // are.
            RefreshRevealHeaderState();
            FilterChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Rebuilds the category choices from the rows, so the list offers what this game actually
        /// uses rather than every category in the library. Ordered to match the category tree, with
        /// anything unknown to it falling in alphabetically after.
        /// </summary>
        /// <remarks>
        /// Only the option set is rebuilt. The ticks are kept as the user left them, minus any
        /// whose category the game no longer has -- dropping the whole selection on a rebuild
        /// would clear the filter every time an edit rewrote the category list.
        /// </remarks>
        private void RebuildFilterOptions()
        {
            _categoryFilterOptions = AssignableCategoryOptions
                .Where(option => !string.IsNullOrWhiteSpace(option))
                .ToList();

            var removed = _selectedCategoryFilters
                .Where(selected => !_categoryFilterOptions.Contains(selected, StringComparer.OrdinalIgnoreCase))
                .ToList();
            foreach (var stale in removed)
            {
                _selectedCategoryFilters.Remove(stale);
            }

            OnPropertyChanged(nameof(FilterOptionsChanged));
            CategoryFilter?.Refresh();
            TypeFilter?.Refresh();
            CustomizationFilter?.Refresh();

            if (removed.Count > 0)
            {
                NotifyFilterChanged();
            }
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
            ResetOrderCommand.RaiseCanExecuteChanged();
            AutoCapstoneCommand.RaiseCanExecuteChanged();
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
        private AchievementCustomizationFacet _customizationFacets;
        private string _customizationToolTip;
        private bool _providerBaselinesKnown;
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
        private bool _isAutoCapstone;
        // Display-only, and only ever set on the bulk proxy: the selected rows disagree on this
        // facet, so the pane shows nothing rather than a value that would be applied.
        private bool _filterScopeIsMixed;
        private bool _unlockedIsMixed;
        private bool _hiddenIsMixed;
        private bool _isGoalIsMixed;

        private static readonly string[] TimeModeDisplayNames = { "AM", "PM", "24hr" };

        private AchievementIconRevealStage _iconStage;
        private bool _showHiddenIcon;
        private bool _showLockedIcon = true;
        private bool _showHiddenTitle;
        private bool _showHiddenDescription;
        private bool _showLockedTitle = true;
        private bool _showLockedDescription = true;
        private bool _showHiddenTrophy = true;
        private bool _showLockedTrophy = true;
        private bool _showHiddenPoints = true;
        private bool _showLockedPoints = true;
        private bool _isTitleRevealed;
        private bool _isDescriptionRevealed;
        private bool _isTrophyRevealed;
        private bool _isPointsRevealed;

        public AchievementEditorRow()
        {
            // The rows are the bulk of what an open editor holds, so they are the useful
            // retention signal: a live count that climbs game over game names the leak.
            Common.LeakWatch.Track("Row.manageEditorRow", this);

            // The facets are derived from a dozen other properties, so rather than have each of
            // their setters remember to raise them, the row watches itself. SuppressNotifications
            // silences this during a bulk load, which is why the loader recomputes once at the
            // end -- see RefreshCustomizationState.
            PropertyChanged += (sender, args) =>
            {
                // An empty name is WPF's "everything changed", raised by ApplyDefinition.
                if (string.IsNullOrEmpty(args?.PropertyName) ||
                    CustomizationInputProperties.Contains(args.PropertyName))
                {
                    RefreshCustomizationState();
                }
            };
        }

        public string OriginalApiName { get; private set; }

        /// <summary>
        /// The provider's own key for the achievement, shown under the details pane. Authored rows
        /// carry a generated key that means nothing outside the plugin, and the bulk proxy has none,
        /// so both read as empty. A hidden row withholds it for the same reason it withholds the
        /// description: the key is often the spoiler.
        /// </summary>
        public string ApiNameResolved =>
            IsProviderRow && !IsBulkRow && !IsBlank && !IsDescriptionHidden
                ? OriginalApiName
                : string.Empty;

        public bool HasApiName => !string.IsNullOrWhiteSpace(ApiNameResolved);

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

        /// <summary>
        /// How much of this row's icon is currently shown. Assigning a stage the row does not have
        /// settles on the next one it does, so a caller -- the column header, say -- can ask every
        /// row for the same stage and let each take what applies to it.
        /// </summary>
        public AchievementIconRevealStage IconStage
        {
            get => ClampIconStage(_iconStage);
            set
            {
                var clamped = ClampIconStage(value);
                if (_iconStage == clamped)
                {
                    return;
                }

                _iconStage = clamped;
                OnPropertyChanged(nameof(IconStage));
                NotifyRevealStateChanged();
            }
        }

        /// <summary>
        /// Whether a display setting is covering this row's icon at all. Either masking puts the
        /// same step in the cycle -- the cover -- because to the user they are one thing: the art
        /// is not being shown yet.
        /// </summary>
        private bool HasCoveredIconStage =>
            !Unlocked && ((Hidden && !ShowHiddenIcon) || !ShowLockedIcon);

        /// <summary>
        /// Whether this row has a locked view worth stepping through. Every locked achievement
        /// does: the step draws its own art the way a player sees it while locked. An unlocked
        /// achievement has no such view -- its art is simply its art.
        /// </summary>
        private bool HasLockedIconStage => !Unlocked;

        /// <summary>
        /// The first stage at or after the one asked for that this row actually has, so the stored
        /// stage can never describe a mask the row is not applying.
        /// </summary>
        private AchievementIconRevealStage ClampIconStage(AchievementIconRevealStage stage)
        {
            if (stage <= AchievementIconRevealStage.Covered && HasCoveredIconStage)
            {
                return AchievementIconRevealStage.Covered;
            }

            if (stage <= AchievementIconRevealStage.Locked && HasLockedIconStage)
            {
                return AchievementIconRevealStage.Locked;
            }

            return AchievementIconRevealStage.Unlocked;
        }

        /// <summary>
        /// Mirrors the grid display setting: when false, a locked hidden row masks its name until
        /// revealed, so opening the editor does not spoil what the achievement is.
        /// </summary>
        public bool ShowHiddenTitle
        {
            get => _showHiddenTitle;
            set
            {
                if (SetValueAndReturn(ref _showHiddenTitle, value))
                {
                    NotifyRevealStateChanged();
                }
            }
        }

        /// <summary>
        /// Mirrors the grid display setting: when false, a locked hidden row masks its description
        /// until revealed.
        /// </summary>
        public bool ShowHiddenDescription
        {
            get => _showHiddenDescription;
            set
            {
                if (SetValueAndReturn(ref _showHiddenDescription, value))
                {
                    NotifyRevealStateChanged();
                }
            }
        }

        /// <summary>
        /// Mirrors the grid display settings for a row that is locked but not hidden. These four
        /// default to revealing, so a row only masks what the Spoilers page says to mask.
        /// </summary>
        public bool ShowLockedTitle
        {
            get => _showLockedTitle;
            set
            {
                if (SetValueAndReturn(ref _showLockedTitle, value))
                {
                    NotifyRevealStateChanged();
                }
            }
        }

        public bool ShowLockedDescription
        {
            get => _showLockedDescription;
            set
            {
                if (SetValueAndReturn(ref _showLockedDescription, value))
                {
                    NotifyRevealStateChanged();
                }
            }
        }

        public bool ShowHiddenTrophy
        {
            get => _showHiddenTrophy;
            set
            {
                if (SetValueAndReturn(ref _showHiddenTrophy, value))
                {
                    NotifyRevealStateChanged();
                }
            }
        }

        public bool ShowLockedTrophy
        {
            get => _showLockedTrophy;
            set
            {
                if (SetValueAndReturn(ref _showLockedTrophy, value))
                {
                    NotifyRevealStateChanged();
                }
            }
        }

        public bool ShowHiddenPoints
        {
            get => _showHiddenPoints;
            set
            {
                if (SetValueAndReturn(ref _showHiddenPoints, value))
                {
                    NotifyRevealStateChanged();
                }
            }
        }

        public bool ShowLockedPoints
        {
            get => _showLockedPoints;
            set
            {
                if (SetValueAndReturn(ref _showLockedPoints, value))
                {
                    NotifyRevealStateChanged();
                }
            }
        }

        /// <summary>
        /// Whether the name and the description are revealed. They are tracked apart because each
        /// has its own toggle beside it, and reading one is not a reason to spoil the other.
        /// </summary>
        public bool IsTitleRevealed
        {
            get => _isTitleRevealed;
            set
            {
                if (SetValueAndReturn(ref _isTitleRevealed, value))
                {
                    NotifyRevealStateChanged();
                }
            }
        }

        public bool IsDescriptionRevealed
        {
            get => _isDescriptionRevealed;
            set
            {
                if (SetValueAndReturn(ref _isDescriptionRevealed, value))
                {
                    NotifyRevealStateChanged();
                }
            }
        }

        public bool IsTrophyRevealed
        {
            get => _isTrophyRevealed;
            set
            {
                if (SetValueAndReturn(ref _isTrophyRevealed, value))
                {
                    NotifyRevealStateChanged();
                }
            }
        }

        public bool IsPointsRevealed
        {
            get => _isPointsRevealed;
            set
            {
                if (SetValueAndReturn(ref _isPointsRevealed, value))
                {
                    NotifyRevealStateChanged();
                }
            }
        }

        /// <summary>True while a placeholder is covering the art rather than the art showing.</summary>
        public bool IsIconCovered => IconStage == AchievementIconRevealStage.Covered;

        public bool IsIconStageLocked => IconStage == AchievementIconRevealStage.Locked;

        public bool IsIconStageUnlocked => IconStage == AchievementIconRevealStage.Unlocked;

        /// <summary>True when this row has more than its own unlocked art to show.</summary>
        public bool CanReveal => HasCoveredIconStage || HasLockedIconStage;

        /// <summary>
        /// Whether this row has a field worth masking at all, and only while the display setting
        /// says not to reveal it -- with the setting on there is nothing to reveal, so the toggle
        /// beside it is not shown either. A hidden achievement is also locked, so either column can
        /// mask it: the locked toggle covers every locked row and the hidden toggle adds the hidden
        /// ones, matching the grid.
        /// </summary>
        public bool CanRevealTitle => !Unlocked && (!ShowLockedTitle || (Hidden && !ShowHiddenTitle));

        public bool CanRevealDescription =>
            !Unlocked && (!ShowLockedDescription || (Hidden && !ShowHiddenDescription));

        /// <summary>
        /// Trophy and points additionally require the row to carry one, so a row with no grade or
        /// no point value never offers a toggle that would reveal nothing.
        /// </summary>
        public bool CanRevealTrophy =>
            !Unlocked && HasTrophyType && (!ShowLockedTrophy || (Hidden && !ShowHiddenTrophy));

        public bool CanRevealPoints =>
            !Unlocked && HasPoints && (!ShowLockedPoints || (Hidden && !ShowHiddenPoints));

        /// <summary>True when the row carries a trophy grade at all.</summary>
        public bool HasTrophyType => !string.IsNullOrWhiteSpace(TrophyType);

        /// <summary>
        /// True when the row carries a point value worth rendering. The editor keeps points as
        /// entered text, so a blank or a zero has nothing to mask.
        /// </summary>
        public bool HasPoints =>
            AchievementEditorFieldRules.TryParsePoints(PointsText, out var points) && points != 0;

        public bool IsTitleHidden => CanRevealTitle && !IsTitleRevealed;

        public bool IsDescriptionHidden => CanRevealDescription && !IsDescriptionRevealed;

        public bool IsTrophyHidden => CanRevealTrophy && !IsTrophyRevealed;

        public bool IsPointsHidden => CanRevealPoints && !IsPointsRevealed;

        /// <summary>
        /// Steps to the next stage this row has, wrapping from its own art back to the most masked
        /// one so the same control both reveals and re-masks.
        /// </summary>
        public void AdvanceIconStage()
        {
            if (!CanReveal)
            {
                return;
            }

            IconStage = IconStage == AchievementIconRevealStage.Unlocked
                ? AchievementIconRevealStage.Covered
                : IconStage + 1;
        }

        /// <summary>
        /// Reveals the name without the option of masking it again, for clicking the placeholder
        /// itself: the click can only mean "show me", and the text is gone once it lands.
        /// </summary>
        public void RevealTitle()
        {
            if (CanRevealTitle)
            {
                IsTitleRevealed = true;
            }
        }

        /// <summary>Reveals the description. See <see cref="RevealTitle"/>.</summary>
        public void RevealDescription()
        {
            if (CanRevealDescription)
            {
                IsDescriptionRevealed = true;
            }
        }

        public void ToggleTitleReveal()
        {
            if (CanRevealTitle)
            {
                IsTitleRevealed = !IsTitleRevealed;
            }
        }

        public void ToggleDescriptionReveal()
        {
            if (CanRevealDescription)
            {
                IsDescriptionRevealed = !IsDescriptionRevealed;
            }
        }

        /// <summary>Reveals the trophy grade. See <see cref="RevealTitle"/>.</summary>
        public void RevealTrophy()
        {
            if (CanRevealTrophy)
            {
                IsTrophyRevealed = true;
            }
        }

        /// <summary>Reveals the point value. See <see cref="RevealTitle"/>.</summary>
        public void RevealPoints()
        {
            if (CanRevealPoints)
            {
                IsPointsRevealed = true;
            }
        }

        public void ToggleTrophyReveal()
        {
            if (CanRevealTrophy)
            {
                IsTrophyRevealed = !IsTrophyRevealed;
            }
        }

        public void TogglePointsReveal()
        {
            if (CanRevealPoints)
            {
                IsPointsRevealed = !IsPointsRevealed;
            }
        }

        /// <summary>
        /// Raised once after any reveal state settles. A single toggle moves several of the
        /// properties below, so a listener that summarizes them over every row -- the column
        /// headers -- reads them once per change rather than once per property.
        /// </summary>
        public event EventHandler RevealStateChanged;

        /// <summary>
        /// Told the property name, the value being replaced and the value replacing it, just
        /// before each change. Set by the view model while the row is attached so its history can
        /// remember what a field was; a plain field rather than an event because exactly one
        /// observer is meaningful and it has to come off with the row.
        /// </summary>
        internal Action<AchievementEditorRow, string, object, object> ValueChanging { get; set; }

        protected override void OnValueChanging(string propertyName, object oldValue, object newValue)
        {
            // Suppressed while the row is being loaded from the store, which is when its values
            // are being restated rather than edited.
            if (!SuppressNotifications)
            {
                ValueChanging?.Invoke(this, propertyName, oldValue, newValue);
            }
        }

        private void NotifyRevealStateChanged()
        {
            OnPropertyChanged(nameof(IconStage));
            OnPropertyChanged(nameof(IsIconCovered));
            OnPropertyChanged(nameof(IsIconStageLocked));
            OnPropertyChanged(nameof(IsIconStageUnlocked));
            OnPropertyChanged(nameof(CanReveal));
            OnPropertyChanged(nameof(CanRevealTitle));
            OnPropertyChanged(nameof(CanRevealDescription));
            OnPropertyChanged(nameof(CanRevealTrophy));
            OnPropertyChanged(nameof(CanRevealPoints));
            OnPropertyChanged(nameof(IsTitleHidden));
            OnPropertyChanged(nameof(IsDescriptionHidden));
            OnPropertyChanged(nameof(IsTrophyHidden));
            OnPropertyChanged(nameof(IsPointsHidden));
            OnPropertyChanged(nameof(DisplayIcon));
            OnPropertyChanged(nameof(ApiNameResolved));
            OnPropertyChanged(nameof(HasApiName));
            RevealStateChanged?.Invoke(this, EventArgs.Empty);
        }

        private string _categoryLabel;
        private string _categoryTypeValue;
        private bool _isCapstone;
        private string _capstoneReplacesDisplayName;
        private bool _suppressCapstonePersist;
        private string _capstoneCategoryDisplayName;
        private string _effectiveCategoryCapstoneName;
        private string _gameWideCapstoneName;

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
        /// <summary>
        /// Set on the bulk proxy when the selected achievements sit in distinct categories, so a
        /// capstone edit across them cannot have them displace each other.
        /// </summary>
        public bool AllowBulkCapstone { get; set; }

        public bool CanEditCapstone => CanEditAssignments && (!IsBulkRow || AllowBulkCapstone);

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
        /// True for the achievement Auto Capstone authored. Its rarity and unlock are kept in step
        /// with the achievements it stands for, so the editor stops offering those two for editing
        /// rather than accepting changes a refresh would undo.
        /// </summary>
        public bool IsAutoCapstone
        {
            get => _isAutoCapstone;
            set
            {
                if (SetValueAndReturn(ref _isAutoCapstone, value))
                {
                    OnPropertyChanged(nameof(CanEditRarity));
                    OnPropertyChanged(nameof(CanEditUnlocked));
                }
            }
        }

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
                var assigned = AchievementCategoryTypeHelper.NormalizeCategory(CategoryLabel);
                if (assigned != null)
                {
                    return assigned;
                }

                // The proxy has no provider category of its own, so there is nothing to fall back
                // to: a blank value means the selected rows disagree, and answering Default would
                // state a category none of them may be in - and, read back by the pane, file them
                // all under it. Disagreement is an absence here, the way the filter scope proxy
                // carries a value no list item matches.
                return IsBulkRow
                    ? null
                    : AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(ProviderCategoryLabel);
            }
        }

        /// <summary>
        /// The category type the achievement actually carries: the user's override when they set
        /// one, otherwise the provider's own.
        /// </summary>
        /// <remarks>
        /// Blank on the bulk proxy when the selection disagrees, for the reason given on
        /// <see cref="EffectiveCategoryLabel"/>: it has no provider value to fall back to, and
        /// Default is a real type rather than a stand-in for "these differ".
        /// </remarks>
        public string EffectiveCategoryTypeValue
        {
            get
            {
                var assigned = AchievementCategoryTypeHelper.Normalize(CategoryTypeValue);
                if (assigned != null)
                {
                    return assigned;
                }

                return IsBulkRow
                    ? null
                    : AchievementCategoryTypeHelper.NormalizeOrDefault(ProviderCategoryTypeValue);
            }
        }

        /// <summary>
        /// The achievement's position in the provider's own order, stamped by the loader before
        /// the user's order is applied so reverting a row can put it back where the provider had
        /// it rather than at the end.
        /// </summary>
        public int ProviderOrderIndex { get; set; } = int.MaxValue;

        /// <summary>
        /// The provider's own values for the fields the editor lets the user override, captured
        /// before any override is applied. A row shows the effective value, so these are the only
        /// way to tell an edited field from one the provider supplied.
        /// </summary>
        public string ProviderDisplayName { get; internal set; }

        /// <inheritdoc cref="ProviderDisplayName"/>
        public string ProviderDescription { get; internal set; }

        /// <inheritdoc cref="ProviderDisplayName"/>
        public int? ProviderPoints { get; internal set; }

        /// <inheritdoc cref="ProviderDisplayName"/>
        public string ProviderTrophyType { get; internal set; }

        /// <inheritdoc cref="ProviderDisplayName"/>
        public DateTime? ProviderUnlockTimeUtc { get; internal set; }

        /// <summary>
        /// Whether the provider calls this achievement a capstone. A game whose capstones have
        /// been materialized no longer consults it, but the row still needs it to tell a capstone
        /// the user set from one that came with the game.
        /// </summary>
        public bool ProviderIsCapstone { get; internal set; }

        /// <summary>
        /// Which facets of this achievement carry user customization. Recomputed as the row is
        /// edited, so the grid's marker tracks an edit without waiting for a reload.
        /// </summary>
        public AchievementCustomizationFacet CustomizationFacets
        {
            get => _customizationFacets;
            private set
            {
                if (SetValueAndReturn(ref _customizationFacets, value))
                {
                    // Built here rather than in the getter: the grid reads it once per row
                    // realization, and rebuilding it there meant a string lookup per facet every
                    // time a row scrolled back into view.
                    _customizationToolTip = BuildCustomizationToolTip(value);
                    OnPropertyChanged(nameof(IsCustomized));
                    OnPropertyChanged(nameof(IsAuthored));
                    OnPropertyChanged(nameof(CustomizationToolTip));
                }
            }
        }

        /// <summary>True when the user has customized this achievement in any way.</summary>
        public bool IsCustomized => CustomizationFacets != AchievementCustomizationFacet.None;

        /// <summary>
        /// True when the user authored this achievement outright. Marked apart from an edited
        /// provider achievement because reverting it deletes it rather than restoring anything.
        /// </summary>
        public bool IsAuthored =>
            (CustomizationFacets & AchievementCustomizationFacet.Authored) != 0;

        /// <summary>
        /// Names what was customized, one facet per line under a heading. Null on an untouched
        /// row, which leaves the marker's cell without a tooltip.
        /// </summary>
        public string CustomizationToolTip => _customizationToolTip;

        /// <summary>
        /// Recomputes <see cref="CustomizationFacets"/>. Called for the row's own edits through
        /// the property-changed hook, and by the loader once the provider baselines are stamped --
        /// those are plain setters, and the load runs with notifications suppressed.
        /// </summary>

        public void RefreshCustomizationState()
        {
            // Without the provider's values every field would read as differing from null, so the
            // whole grid would mark itself customized. The loader sets this once the raw snapshot
            // has been seen.
            if (!_providerBaselinesKnown)
            {
                CustomizationFacets = IsProviderRow
                    ? AchievementCustomizationFacet.None
                    : AchievementCustomizationFacet.Authored;
                return;
            }

            // The provider icon baselines arrive after the row is built, and the slots read
            // them to decide whether they are showing the user's art or the provider's.
            OnPropertyChanged(nameof(UnlockedIconDisplayText));
            OnPropertyChanged(nameof(LockedIconDisplayText));

            AchievementEditorFieldRules.TryParsePoints(PointsText, out var points);
            CustomizationFacets = AchievementCustomizationRules.Resolve(new AchievementCustomizationInputs
            {
                IsAuthored = !IsProviderRow,
                DisplayName = NormalizeRowText(DisplayName),
                ProviderDisplayName = NormalizeRowText(ProviderDisplayName),
                Description = NormalizeRowText(Description),
                ProviderDescription = NormalizeRowText(ProviderDescription),
                Points = points,
                ProviderPoints = ProviderPoints,
                TrophyType = NormalizeRowText(TrophyType),
                ProviderTrophyType = NormalizeRowText(ProviderTrophyType),
                UnlockTimeUtc = UnlockTime,
                ProviderUnlockTimeUtc = ProviderUnlockTimeUtc,
                // The effective values, not CategoryLabel and CategoryTypeValue: those two hold
                // the user's assignment alone and are blank on a row carrying none, so comparing
                // them against the provider's own read every untouched achievement of a
                // categorized game as customized.
                Category = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(EffectiveCategoryLabel),
                ProviderCategory = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(ProviderCategoryLabel),
                CategoryType = AchievementCategoryTypeHelper.NormalizeOrDefault(EffectiveCategoryTypeValue),
                ProviderCategoryType = AchievementCategoryTypeHelper.NormalizeOrDefault(ProviderCategoryTypeValue),
                Note = AchievementNote,
                UnlockedIconPath = NormalizeRowText(UnlockedIconPath),
                ProviderUnlockedIconPath = NormalizeRowText(ProviderUnlockedIconPath),
                LockedIconPath = NormalizeRowText(LockedIconPath),
                ProviderLockedIconPath = NormalizeRowText(ProviderLockedIconPath),
                Hidden = Hidden,
                ProviderHidden = ProviderHidden,
                IsFiltered = IsFiltered,
                IsSummaryFiltered = IsSummaryFiltered,
                IsGoal = IsGoal,
                IsCapstone = IsCapstone,
                ProviderIsCapstone = ProviderIsCapstone
            });
        }

        /// <summary>
        /// Lets the loader say the provider's values are in hand. Until they are the row reports
        /// no customization rather than guessing against nulls.
        /// </summary>
        public void MarkProviderBaselinesKnown()
        {
            _providerBaselinesKnown = true;
        }

        /// <summary>
        /// Every property the facets are computed from. A change to one of these recomputes them;
        /// anything else -- a reveal step, a validation message -- leaves them alone.
        /// </summary>
        private static readonly HashSet<string> CustomizationInputProperties = new HashSet<string>(
            StringComparer.Ordinal)
        {
            nameof(DisplayName),
            nameof(Description),
            nameof(PointsText),
            nameof(TrophyType),
            nameof(UnlockTime),
            nameof(CategoryLabel),
            nameof(CategoryTypeValue),
            nameof(AchievementNote),
            nameof(UnlockedIconPath),
            nameof(LockedIconPath),
            nameof(Hidden),
            nameof(IsFiltered),
            nameof(IsSummaryFiltered),
            nameof(IsGoal),
            nameof(IsCapstone),
            nameof(IsProviderRow)
        };

        private static string NormalizeRowText(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        /// <summary>
        /// The localized name of each facet, in the order the tooltip lists them. Every label is
        /// an existing key: the same words the editor's own fields carry, so the tooltip names
        /// what it points at.
        /// </summary>
        private static readonly Tuple<AchievementCustomizationFacet, string>[] CustomizationFacetLabelKeys =
        {
            Tuple.Create(AchievementCustomizationFacet.DisplayName, "LOCPlayAch_Column_AchievementName"),
            Tuple.Create(AchievementCustomizationFacet.Description, "LOCPlayAch_Column_Description"),
            Tuple.Create(AchievementCustomizationFacet.UnlockedIcon, "LOCPlayAch_ManageAchievements_Custom_UnlockedIcon"),
            Tuple.Create(AchievementCustomizationFacet.LockedIcon, "LOCPlayAch_ManageAchievements_Custom_LockedIcon"),
            Tuple.Create(AchievementCustomizationFacet.Points, "LOCPlayAch_Column_Points"),
            Tuple.Create(AchievementCustomizationFacet.TrophyType, "LOCPlayAch_Column_Trophy"),
            Tuple.Create(AchievementCustomizationFacet.UnlockTime, "LOCPlayAch_Common_UnlockTime"),
            Tuple.Create(AchievementCustomizationFacet.Category, "LOCPlayAch_Common_Label_Category"),
            Tuple.Create(AchievementCustomizationFacet.CategoryType, "LOCPlayAch_ManageAchievements_Category_TypeSelectorLabel"),
            Tuple.Create(AchievementCustomizationFacet.Hidden, "LOCPlayAch_ManageAchievements_Custom_Hidden"),
            Tuple.Create(AchievementCustomizationFacet.Note, "LOCPlayAch_ManageAchievements_Notes_Note"),
            Tuple.Create(AchievementCustomizationFacet.FilterScope, "LOCPlayAch_Menu_Filters"),
            Tuple.Create(AchievementCustomizationFacet.Goal, "LOCPlayAch_ManageAchievements_Editor_Goal"),
            Tuple.Create(AchievementCustomizationFacet.Capstone, "LOCPlayAch_Dynamic_Capstone")
        };

        /// <summary>
        /// One facet per line under the same word the filter uses, so the marker, the filter and
        /// the editor's fields all read alike. An authored row is named for what it is instead:
        /// it has no provider values behind it to have diverged from.
        /// </summary>
        private static string BuildCustomizationToolTip(AchievementCustomizationFacet facets)
        {
            if (facets == AchievementCustomizationFacet.None)
            {
                return null;
            }

            if ((facets & AchievementCustomizationFacet.Authored) != 0)
            {
                return ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Tab_Custom");
            }

            var lines = new List<string> { ResourceProvider.GetString("LOCPlayAch_Tagging_Customized") };
            foreach (var entry in CustomizationFacetLabelKeys)
            {
                if ((facets & entry.Item1) != 0)
                {
                    lines.Add(ResourceProvider.GetString(entry.Item2));
                }
            }

            return string.Join(Environment.NewLine, lines);
        }

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
            ManageAchievements.AchievementEditorFieldRules.CanEditRarity(
                isCustomRow: !IsProviderRow,
                isAutoCapstone: IsAutoCapstone);

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
                isManuallyTrackedGame: IsManuallyTrackedGame,
                isAutoCapstone: IsAutoCapstone);

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
                    OnPropertyChanged(nameof(FilterScopeDisplayText));
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
                    OnPropertyChanged(nameof(FilterScopeDisplayText));
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
                OnPropertyChanged(nameof(FilterScopeDisplayText));
            }
        }

        /// <summary>
        /// The scope's name, for the Filter column's button face. Uses the same strings the
        /// details pane's list offers, and is blank for the proxy's mixed state, which stands for
        /// disagreement rather than for a scope.
        /// </summary>
        public string FilterScopeDisplayText
        {
            get
            {
                switch (FilterScope)
                {
                    case AchievementFilterScope.All:
                        return ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Filters_FilterOut");
                    case AchievementFilterScope.Summary:
                        return ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Filters_FilterOutOfSummaries");
                    case AchievementFilterScope.None:
                        return ResourceProvider.GetString("LOCPlayAch_Common_None");
                    default:
                        return string.Empty;
                }
            }
        }

        public string CategoryLabel
        {
            get => _categoryLabel;
            set
            {
                if (SetValueAndReturn(ref _categoryLabel, value))
                {
                    OnPropertyChanged(nameof(EffectiveCategoryLabel));
                }
            }
        }

        public string CategoryTypeValue
        {
            get => _categoryTypeValue;
            set
            {
                if (SetValueAndReturn(ref _categoryTypeValue, value))
                {
                    OnPropertyChanged(nameof(EffectiveCategoryTypeValue));
                    OnPropertyChanged(nameof(CategoryTypeDisplayText));
                }
            }
        }

        public string CategoryTypeDisplayText
        {
            get
            {
                // The Default sentinel renders blank in grid cells; a button needs a label. Except
                // on the bulk proxy, where a blank effective value means the selection disagrees
                // and the button has to stay empty rather than claim they are all Default.
                var text = AchievementCategoryTypeHelper.ToDisplayText(EffectiveCategoryTypeValue);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }

                return IsBulkRow && string.IsNullOrWhiteSpace(EffectiveCategoryTypeValue)
                    ? string.Empty
                    : AchievementCategoryTypeHelper.ToCategoryTypeDisplayText(AchievementCategoryTypeHelper.NormalizeOrDefault(null));
            }
        }

        public bool IsCapstone
        {
            get => _isCapstone;
            set
            {
                if (SetValueAndReturn(ref _isCapstone, value))
                {
                    RaiseCapstoneReadoutChanged();
                }
            }
        }

        /// <summary>
        /// What acting on this achievement would do to the game's capstones: add one, replace the
        /// one already standing for its category, or drop it.
        /// </summary>
        public string CapstoneActionText
        {
            get
            {
                if (IsCapstone)
                {
                    return ResourceProvider.GetString("LOCPlayAch_Button_Remove");
                }

                return string.IsNullOrWhiteSpace(CapstoneReplacesDisplayName)
                    ? ResourceProvider.GetString("LOCPlayAch_Button_Add")
                    : ResourceProvider.GetString("LOCPlayAch_Button_Replace");
            }
        }

        /// <summary>
        /// The same action as a single character, for the grid cell, where a word would need most
        /// of the column. The details pane keeps <see cref="CapstoneActionText"/>: there the width
        /// is free and the word is clearer.
        /// </summary>
        /// <remarks>
        /// Plain characters rather than icon-font codepoints, so the glyph survives a theme that
        /// substitutes the font: a plus adds, a minus removes, and a double arrow replaces, with
        /// <see cref="CapstoneActionToolTip"/> still naming what will happen.
        /// </remarks>
        public string CapstoneActionGlyph
        {
            get
            {
                if (IsCapstone)
                {
                    return "−";
                }

                return string.IsNullOrWhiteSpace(CapstoneReplacesDisplayName)
                    ? "+"
                    : "↔";
            }
        }

        /// <summary>
        /// The capstone that acting on this achievement would displace, named on the button tooltip
        /// so a replacement is never a surprise. Null when nothing would be displaced.
        /// </summary>
        public string CapstoneReplacesDisplayName
        {
            get => _capstoneReplacesDisplayName;
            set
            {
                if (SetValueAndReturn(ref _capstoneReplacesDisplayName, value))
                {
                    OnPropertyChanged(nameof(CapstoneActionText));
                    OnPropertyChanged(nameof(CapstoneActionGlyph));
                    OnPropertyChanged(nameof(CapstoneActionToolTip));
                }
            }
        }

        public string CapstoneActionToolTip =>
            string.IsNullOrWhiteSpace(CapstoneReplacesDisplayName)
                ? null
                : string.Format(
                    ResourceProvider.GetString("LOCPlayAch_Capstone_Replaces"),
                    CapstoneReplacesDisplayName);

        /// <summary>The display path of this achievement's own category.</summary>
        public string CapstoneCategoryDisplayName
        {
            get => _capstoneCategoryDisplayName;
            private set => SetValue(ref _capstoneCategoryDisplayName, value);
        }

        /// <summary>
        /// What this row's own category stands on, which may be a capstone inherited from an
        /// ancestor category rather than anything in this category at all.
        /// </summary>
        public string CurrentCategoryCapstoneText =>
            string.Format(
                ResourceProvider.GetString("LOCPlayAch_Capstone_CurrentCategory"),
                _effectiveCategoryCapstoneName);

        /// <summary>
        /// What stands for the whole game, which only reads that way while the game holds exactly
        /// one capstone.
        /// </summary>
        public string CurrentGameCapstoneText =>
            string.Format(
                ResourceProvider.GetString("LOCPlayAch_Capstone_CurrentGame"),
                _gameWideCapstoneName);

        /// <summary>
        /// Shown only when there is a capstone to name and it is not this row: the checkbox beside
        /// it already says when this achievement is the one, and a line reading None says nothing
        /// the cleared checkbox has not. The proxy reports on no single category at all.
        /// </summary>
        public bool ShowCurrentCategoryCapstone =>
            !IsBulkRow && !string.IsNullOrWhiteSpace(_effectiveCategoryCapstoneName) && !IsCapstone;

        /// <summary>Shown on the same terms, for the capstone standing for the whole game.</summary>
        public bool ShowCurrentGameCapstone =>
            !IsBulkRow && !string.IsNullOrWhiteSpace(_gameWideCapstoneName) && !IsCapstone;

        /// <summary>
        /// Applies resolved capstone state without writing it back, for the refresh that re-reads
        /// the store.
        /// </summary>
        public void SetCapstoneStateFromSource(
            bool isCapstone,
            string categoryDisplayName,
            string categoryCapstoneName,
            string gameCapstoneName)
        {
            _capstoneCategoryDisplayName = categoryDisplayName;
            _effectiveCategoryCapstoneName = categoryCapstoneName;
            _gameWideCapstoneName = gameCapstoneName;
            _suppressCapstonePersist = true;
            try
            {
                IsCapstone = isCapstone;
            }
            finally
            {
                _suppressCapstonePersist = false;
            }

            OnPropertyChanged(nameof(CapstoneCategoryDisplayName));
            RaiseCapstoneReadoutChanged();
        }

        /// <summary>
        /// True while the row is being re-seeded from the store, so the write-back hook can tell a
        /// refresh apart from a click.
        /// </summary>
        internal bool SuppressCapstonePersist => _suppressCapstonePersist;

        private void RaiseCapstoneReadoutChanged()
        {
            OnPropertyChanged(nameof(CapstoneActionText));
            OnPropertyChanged(nameof(CapstoneActionToolTip));
            OnPropertyChanged(nameof(CapstoneActionGlyph));
            OnPropertyChanged(nameof(CurrentCategoryCapstoneText));
            OnPropertyChanged(nameof(CurrentGameCapstoneText));
            OnPropertyChanged(nameof(ShowCurrentCategoryCapstone));
            OnPropertyChanged(nameof(ShowCurrentGameCapstone));
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
            OnPropertyChanged(nameof(FilterScopeDisplayText));
        }

        /// <summary>
        /// Seeds the rarity from what it was derived from, bypassing the guard on the public
        /// setter for the same reason the unlock seeder does.
        /// </summary>
        internal void SetRarityFromSource(double? globalPercentUnlocked, string rarity)
        {
            _globalPercentUnlockedText = globalPercentUnlocked.HasValue
                ? globalPercentUnlocked.Value.ToString(CultureInfo.InvariantCulture)
                : null;
            _rarity = rarity;
            _rarityInput = _globalPercentUnlockedText ?? rarity;
            _rarityInputInvalid = false;
            OnPropertyChanged(nameof(GlobalPercentUnlockedText));
            OnPropertyChanged(nameof(Rarity));
            OnPropertyChanged(nameof(RarityInput));
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
        /// A blank is only ever a display state: the ticks are not three-state, so no click can
        /// land on it, setting one back to blank is ignored, and picking either real value applies
        /// it even when it matches what the blank was standing in front of.
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

        /// <summary>
        /// The icon slot's text: the user's own art, or blank where the achievement is still
        /// showing the provider's.
        /// </summary>
        /// <remarks>
        /// A row carries its effective icon, so this field used to read back the provider's path
        /// on every untouched achievement -- a long string the user cannot act on, and which
        /// makes a slot holding nothing look like a slot holding something. Blank says what is
        /// true: there is no override here. The path in use is still on the slot's tooltip.
        /// </remarks>
        public string UnlockedIconDisplayText
        {
            get => IsIconOverride(UnlockedIconPath, ProviderUnlockedIconPath)
                ? ToIconDisplayText(UnlockedIconPath)
                : string.Empty;
            set => UnlockedIconPath = FromIconDisplayText(value);
        }

        /// <inheritdoc cref="UnlockedIconDisplayText"/>
        public string LockedIconDisplayText
        {
            get => IsIconOverride(LockedIconPath, ProviderLockedIconPath)
                ? ToIconDisplayText(LockedIconPath)
                : string.Empty;
            set => LockedIconPath = FromIconDisplayText(value);
        }

        /// <summary>
        /// Whether a slot holds the user's art rather than the provider's. The same test the
        /// icon-override writes use, so the field is filled exactly when something is stored.
        /// An authored achievement has no provider art behind it, so its icon is always its own.
        /// </summary>
        private static bool IsIconOverride(string current, string provider)
        {
            return !string.IsNullOrWhiteSpace(current) &&
                   !string.Equals(
                       NormalizeRowText(current),
                       NormalizeRowText(provider),
                       StringComparison.OrdinalIgnoreCase);
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

        // Both re-raise the reveal state: gaining or losing a value flips whether the row has
        // anything to mask, and so whether its reveal toggle should be offered at all.
        public string PointsText
        {
            get => _pointsText;
            set
            {
                if (SetValueAndReturn(ref _pointsText, value))
                {
                    OnPropertyChanged(nameof(HasPoints));
                    NotifyRevealStateChanged();
                }
            }
        }

        public string TrophyType
        {
            get => _trophyType;
            set
            {
                if (SetValueAndReturn(ref _trophyType, value))
                {
                    OnPropertyChanged(nameof(HasTrophyType));
                    NotifyRevealStateChanged();
                }
            }
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
                // The stage decides, so the last step shows the unlocked art even for an
                // achievement that is still locked -- which is the point of stepping through:
                // the three looks an icon has, rather than only the one this row happens to be
                // in. The stages a row does not have are already clamped away, so an unlocked
                // achievement only ever reaches the last one.
                switch (IconStage)
                {
                    case AchievementIconRevealStage.Covered:
                        // Hidden wins over locked, the more spoiler-sensitive of the two, matching
                        // AchievementDisplayItem.
                        return Hidden && !ShowHiddenIcon
                            ? AchievementIconResolver.GetHiddenFallbackIcon()
                            : AchievementIconResolver.GetLockedFallbackIcon();

                    case AchievementIconRevealStage.Locked:
                        return AchievementIconResolver.GetLockedDisplayIcon(UnlockedIconPath, OwnLockedArtPath);

                    default:
                        return AchievementIconResolver.GetUnlockedDisplayIcon(UnlockedIconPath);
                }
            }
        }

        private bool _useSeparateLockedIcons;

        /// <summary>
        /// Whether this game shows the provider's separate locked art, seeded from the same
        /// per-game setting the grids read, so the editor previews what the grids will draw.
        /// </summary>
        public bool UseSeparateLockedIcons
        {
            get => _useSeparateLockedIcons;
            set
            {
                if (SetValueAndReturn(ref _useSeparateLockedIcons, value))
                {
                    OnPropertyChanged(nameof(LockedPreviewPath));
                    OnPropertyChanged(nameof(DisplayIcon));
                }
            }
        }

        public string UnlockedPreviewPath => AchievementIconResolver.GetUnlockedDisplayIcon(UnlockedIconPath);

        public string LockedPreviewPath => AchievementIconResolver.GetLockedDisplayIcon(UnlockedIconPath, OwnLockedArtPath);

        /// <summary>
        /// The locked art this row should draw from, or null to derive the locked look from the
        /// unlocked icon.
        /// </summary>
        private string OwnLockedArtPath =>
            AchievementIconResolver.ResolveLockedArtPath(LockedIconPath, UseSeparateLockedIcons);


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
                IsAutoCapstone = IsAutoCapstone,
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
