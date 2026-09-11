using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Search;
using PlayniteAchievements.ViewModels.Items;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

using ObservableObject = PlayniteAchievements.Common.ObservableObject;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    /// <summary>
    /// The merged Manage Achievements editor: one list of every achievement — provider-backed and
    /// user-authored together — with each facet that used to live on its own tab editable per row.
    /// </summary>
    /// <remarks>
    /// Rows come from hydrated game data, so custom achievements are already merged in and the
    /// user's order already applied; that is what lets an authored achievement sit between two
    /// provider ones. Every edit persists on completion through
    /// <see cref="AchievementOverridesService"/>, one field at a time, rather than rewriting the
    /// game's whole customization.
    /// </remarks>
    internal sealed class ManageAchievementsEditorViewModel : ObservableObject
    {
        private readonly Guid _gameId;
        private readonly AchievementOverridesService _achievementOverridesService;
        private readonly ManageAchievementsDataSnapshotProvider _gameDataSnapshotProvider;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;

        private readonly SearchTextIndex<AchievementEditorRow> _searchIndex =
            new SearchTextIndex<AchievementEditorRow>(item =>
                SearchTextBuilder.ForManageNote(
                    item?.DisplayName,
                    item?.Description,
                    item?.ApiName,
                    item?.AchievementNote,
                    item?.CategoryLabelDisplay,
                    item?.CategoryTypeDisplay));

        private List<AchievementEditorRow> _allRows = new List<AchievementEditorRow>();
        private bool _isReloading;
        private bool _hasAchievements;
        private string _searchText = string.Empty;
        private string _statusMessage;
        private bool _statusIsError;
        private AchievementEditorRow _selectedRow;

        public ManageAchievementsEditorViewModel(
            Guid gameId,
            AchievementOverridesService achievementOverridesService,
            ManageAchievementsDataSnapshotProvider gameDataSnapshotProvider,
            PlayniteAchievementsSettings settings,
            ILogger logger)
        {
            _gameId = gameId;
            _achievementOverridesService = achievementOverridesService ??
                throw new ArgumentNullException(nameof(achievementOverridesService));
            _gameDataSnapshotProvider = gameDataSnapshotProvider ??
                throw new ArgumentNullException(nameof(gameDataSnapshotProvider));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger;

            AchievementRows = new BulkObservableCollection<AchievementEditorRow>();
            TrophyTypeOptions = new ObservableCollection<string>(new[] { string.Empty, "bronze", "silver", "gold", "platinum" });
            ClearSearchCommand = new RelayCommand(_ => SearchText = string.Empty);
            ResetOrderCommand = new RelayCommand(_ => ResetCustomOrder());

            ReloadData();
        }

        /// <summary>Raised when an edit changed data other surfaces display.</summary>
        public event EventHandler CustomizationPersisted;

        /// <summary>
        /// Inline message for a points value that is not a non-negative integer. Resolved in one
        /// place so the localized key is not repeated across the field handlers.
        /// </summary>
        private static string InvalidPointsMessage =>
            ResourceProvider.GetString("LOCPlayAch_Common_Validation_NonNegativeInteger");

        /// <summary>
        /// Inline message for an unlock time value that cannot be parsed as a date and time.
        /// </summary>
        private static string InvalidUnlockTimeMessage =>
            ResourceProvider.GetString("LOCPlayAch_Common_Validation_InvalidDateTime");

        public BulkObservableCollection<AchievementEditorRow> AchievementRows { get; }

        public ObservableCollection<string> TrophyTypeOptions { get; }

        public RelayCommand ClearSearchCommand { get; }

        public RelayCommand ResetOrderCommand { get; }

        public bool HasAchievements
        {
            get => _hasAchievements;
            private set => SetValue(ref _hasAchievements, value);
        }

        public AchievementEditorRow SelectedRow
        {
            get => _selectedRow;
            set => SetValue(ref _selectedRow, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            private set
            {
                SetValue(ref _statusMessage, value);
                OnPropertyChanged(nameof(HasStatusMessage));
            }
        }

        public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

        public bool StatusIsError
        {
            get => _statusIsError;
            private set => SetValue(ref _statusIsError, value);
        }

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetValueAndReturn(ref _searchText, value ?? string.Empty))
                {
                    ApplyFilter();
                }
            }
        }

        public void ReloadData()
        {
            _isReloading = true;
            try
            {
                var hydrated = _gameDataSnapshotProvider.GetHydratedGameData();
                var achievements = hydrated?.Achievements?
                    .Where(a => a != null && !string.IsNullOrWhiteSpace(a.ApiName))
                    .ToList() ?? new List<AchievementDetail>();

                // Hydration already merged custom achievements in and applied the user's order, so
                // the list arrives interleaved exactly as it should be displayed.
                var appearance = AchievementDisplayItem.CreateAppearanceSettingsSnapshot(
                    _settings,
                    _gameId,
                    hydrated?.UseSeparateLockedIconsWhenAvailable);
                var categoryMemo = new AchievementDisplayItem.CategoryPresentationMemo();

                DetachRows();
                _allRows = achievements
                    .Select(a => CreateRow(hydrated, a, appearance, categoryMemo))
                    .Where(row => row != null)
                    .ToList();

                foreach (var row in _allRows)
                {
                    row.FieldEdited += Row_FieldEdited;
                }

                _searchIndex.Rebuild(_allRows);
                HasAchievements = _allRows.Count > 0;
                SetStatus(null, false);
                ApplyFilter();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed loading the achievement editor for gameId={_gameId}.");
                DetachRows();
                _allRows = new List<AchievementEditorRow>();
                AchievementRows.ReplaceAll(_allRows);
                HasAchievements = false;
                SetStatus(string.Format(
                    ResourceProvider.GetString("LOCPlayAch_Status_Failed"),
                    ex.Message), true);
            }
            finally
            {
                _isReloading = false;
            }
        }

        private AchievementEditorRow CreateRow(
            GameAchievementData gameData,
            AchievementDetail achievement,
            AchievementDisplayItem.AppearanceSettingsSnapshot appearance,
            AchievementDisplayItem.CategoryPresentationMemo categoryMemo)
        {
            var projected = AchievementDisplayItem.Create(
                gameData,
                achievement,
                _settings,
                playniteGameIdOverride: _gameId,
                appearanceSettings: appearance,
                categoryMemo: categoryMemo);
            if (projected == null)
            {
                return null;
            }

            var row = new AchievementEditorRow
            {
                IsCustomRow = achievement.IsCustom,
                ProviderKey = projected.ProviderKey,
                GameName = projected.GameName,
                SortingName = projected.SortingName,
                PlayniteGameId = projected.PlayniteGameId,
                ApiName = projected.ApiName,
                DisplayName = projected.DisplayName,
                Description = projected.Description,
                UnlockedIconPath = projected.UnlockedIconPath,
                LockedIconPath = projected.LockedIconPath,
                UnlockTimeUtc = projected.UnlockTimeUtc,
                GlobalPercentUnlocked = projected.GlobalPercentUnlocked,
                Rarity = projected.Rarity,
                PointsValue = projected.PointsValue,
                ProgressNum = projected.ProgressNum,
                ProgressDenom = projected.ProgressDenom,
                TrophyType = projected.TrophyType,
                Unlocked = projected.Unlocked,
                Hidden = projected.Hidden,
                IsCapstone = projected.IsCapstone,
                IsGoal = projected.IsGoal,
                AchievementNote = projected.AchievementNote,
                CategoryLabel = projected.CategoryLabel,
                CategoryType = projected.CategoryType,
                ShowHiddenIcon = projected.ShowHiddenIcon,
                ShowHiddenTitle = projected.ShowHiddenTitle,
                ShowHiddenDescription = projected.ShowHiddenDescription,
                ShowHiddenSuffix = projected.ShowHiddenSuffix,
                ShowLockedIcon = projected.ShowLockedIcon,
                ShowRarityBar = projected.ShowRarityBar,
                UseSeparateLockedIconsWhenAvailable = projected.UseSeparateLockedIconsWhenAvailable
            };

            row.LoadEditableFields(achievement.IsFiltered, achievement.IsFilteredFromSummaries);
            return row;
        }

        private void DetachRows()
        {
            foreach (var row in _allRows)
            {
                if (row != null)
                {
                    row.FieldEdited -= Row_FieldEdited;
                }
            }
        }

        private void ApplyFilter()
        {
            IEnumerable<AchievementEditorRow> filtered = _allRows;
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                var searchQuery = SearchQuery.From(SearchText);
                filtered = filtered.Where(row => _searchIndex.Matches(row, searchQuery));
            }

            AchievementRows.ReplaceAll(filtered.ToList());
        }

        /// <summary>
        /// Persists one completed edit. A provider achievement writes an override; an authored one
        /// writes its own definition, because a custom achievement has no provider value to fall
        /// back to.
        /// </summary>
        private void Row_FieldEdited(object sender, AchievementEditorField field)
        {
            if (_isReloading || !(sender is AchievementEditorRow row))
            {
                return;
            }

            try
            {
                row.SetValidationMessage(null);
                if (PersistSharedFacet(row, field))
                {
                    return;
                }

                if (row.IsCustomRow)
                {
                    PersistCustomRowField(row, field);
                }
                else
                {
                    PersistProviderRowField(row, field);
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed persisting {field} for achievement {row.ApiName}.");
                SetStatus(string.Format(
                    ResourceProvider.GetString("LOCPlayAch_Status_Failed"),
                    ex.Message), true);
            }
        }

        /// <summary>
        /// Facets stored the same way for both row kinds: they key off the ApiName rather than
        /// living on a provider payload or a custom definition.
        /// </summary>
        private bool PersistSharedFacet(AchievementEditorRow row, AchievementEditorField field)
        {
            switch (field)
            {
                case AchievementEditorField.Note:
                    _achievementOverridesService.SetAchievementNote(_gameId, row.ApiName, row.EditNote);
                    return true;

                case AchievementEditorField.Filtered:
                case AchievementEditorField.SummaryFiltered:
                    PersistFilters();
                    return true;

                case AchievementEditorField.Goal:
                    _achievementOverridesService.SetAchievementGoal(_gameId, row.ApiName, row.EditIsGoal);
                    return true;

                case AchievementEditorField.Capstone:
                    PersistCapstone(row);
                    return true;

                case AchievementEditorField.Category:
                    _achievementOverridesService.SetAchievementCategoryOverrides(
                        _gameId,
                        BuildFacetMap(r => r.EditCategory));
                    RaisePersisted();
                    return true;

                case AchievementEditorField.CategoryType:
                    _achievementOverridesService.SetAchievementCategoryTypeOverrides(
                        _gameId,
                        BuildFacetMap(r => r.EditCategoryType));
                    RaisePersisted();
                    return true;

                default:
                    return false;
            }
        }

        private void PersistProviderRowField(AchievementEditorRow row, AchievementEditorField field)
        {
            switch (field)
            {
                case AchievementEditorField.DisplayName:
                    Write(row, AchievementEditableField.DisplayName, row.EditDisplayName);
                    break;

                case AchievementEditorField.Description:
                    Write(row, AchievementEditableField.Description, row.EditDescription);
                    break;

                case AchievementEditorField.Points:
                    if (!row.TryGetPoints(out var points))
                    {
                        row.SetValidationMessage(InvalidPointsMessage);
                        return;
                    }

                    Write(row, AchievementEditableField.Points, points);
                    break;

                case AchievementEditorField.TrophyType:
                    Write(row, AchievementEditableField.TrophyType, row.EditTrophyType);
                    break;

                case AchievementEditorField.UnlockTime:
                    if (!row.CanEditUnlockTime)
                    {
                        return;
                    }

                    if (!row.TryGetUnlockTimeUtc(out var unlockTimeUtc))
                    {
                        row.SetValidationMessage(InvalidUnlockTimeMessage);
                        return;
                    }

                    Write(row, AchievementEditableField.UnlockTimeUtc, unlockTimeUtc);
                    break;

                case AchievementEditorField.Rarity:
                    // Provider rarity is derived from the provider's percentages and is not
                    // editable; the row refuses the edit, so reaching here means a wiring mistake.
                    return;
            }
        }

        private void Write(AchievementEditorRow row, AchievementEditableField field, object value)
        {
            _achievementOverridesService.SetAchievementFieldOverride(_gameId, row.ApiName, field, value);
            RaisePersisted();
        }

        /// <summary>
        /// Applies an edit to the authored definition behind a custom row. The whole definition list
        /// is rewritten because that is how custom achievements are stored.
        /// </summary>
        private void PersistCustomRowField(AchievementEditorRow row, AchievementEditorField field)
        {
            var definitions = LoadCustomDefinitions();
            if (definitions == null || definitions.Count == 0)
            {
                return;
            }

            var definition = definitions.FirstOrDefault(d =>
                d != null &&
                string.Equals(
                    CustomAchievementProjectionService.BuildApiName(d.Id),
                    row.ApiName,
                    StringComparison.OrdinalIgnoreCase));
            if (definition == null)
            {
                return;
            }

            switch (field)
            {
                case AchievementEditorField.DisplayName:
                    definition.DisplayName = row.EditDisplayName;
                    break;

                case AchievementEditorField.Description:
                    definition.Description = row.EditDescription;
                    break;

                case AchievementEditorField.Points:
                    if (!row.TryGetPoints(out var points))
                    {
                        row.SetValidationMessage(InvalidPointsMessage);
                        return;
                    }

                    definition.Points = points;
                    break;

                case AchievementEditorField.TrophyType:
                    definition.TrophyType = row.EditTrophyType;
                    break;

                case AchievementEditorField.UnlockTime:
                    if (!row.TryGetUnlockTimeUtc(out var unlockTimeUtc))
                    {
                        row.SetValidationMessage(InvalidUnlockTimeMessage);
                        return;
                    }

                    definition.UnlockTimeUtc = unlockTimeUtc;
                    break;

                case AchievementEditorField.Rarity:
                    definition.Rarity = row.EditRarity;
                    break;

                default:
                    return;
            }

            _achievementOverridesService.SetCustomAchievements(_gameId, definitions);
            RaisePersisted();
        }

        private List<CustomAchievementDefinition> LoadCustomDefinitions() =>
            GameCustomDataLookup.ResolveGameCustomData(_gameId, _settings?.Persisted)?.CustomAchievements;

        /// <summary>
        /// Both filter lists are stored as whole sets, so a single checkbox rewrites them from the
        /// current row state rather than trying to patch one entry.
        /// </summary>
        private void PersistFilters()
        {
            var filtered = _allRows
                .Where(row => row != null && row.EditIsFiltered && !string.IsNullOrWhiteSpace(row.ApiName))
                .Select(row => row.ApiName)
                .ToList();
            var summaryFiltered = _allRows
                .Where(row => row != null && row.EditIsSummaryFiltered && !string.IsNullOrWhiteSpace(row.ApiName))
                .Select(row => row.ApiName)
                .ToList();

            _achievementOverridesService.SetAchievementFilters(_gameId, filtered, summaryFiltered);
            RaisePersisted();
        }

        /// <summary>
        /// A game has at most one capstone, so setting one clears any other row's flag rather than
        /// accumulating several.
        /// </summary>
        private void PersistCapstone(AchievementEditorRow row)
        {
            var apiName = row.EditIsCapstone ? row.ApiName : null;
            if (row.EditIsCapstone)
            {
                foreach (var other in _allRows)
                {
                    if (other != null && !ReferenceEquals(other, row) && other.EditIsCapstone)
                    {
                        other.SuppressFieldEdits(() => other.EditIsCapstone = false);
                    }
                }
            }

            _achievementOverridesService.SetCapstone(_gameId, apiName);
            RaisePersisted();
        }

        private Dictionary<string, string> BuildFacetMap(Func<AchievementEditorRow, string> selector)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in _allRows)
            {
                var apiName = row?.ApiName;
                var value = selector(row);
                if (!string.IsNullOrWhiteSpace(apiName) && !string.IsNullOrWhiteSpace(value))
                {
                    map[apiName] = value;
                }
            }

            return map;
        }

        /// <summary>
        /// Moves the dragged achievements relative to a target row. Custom and provider rows are
        /// the same kind here, which is what lets an authored achievement be positioned between two
        /// provider ones.
        /// </summary>
        public bool MoveItemsByApiName(
            IReadOnlyList<string> draggedApiNames,
            string targetApiName,
            bool insertAfterTarget)
        {
            if (draggedApiNames == null || draggedApiNames.Count == 0 || string.IsNullOrWhiteSpace(targetApiName))
            {
                return false;
            }

            var source = AchievementRows.ToList();
            var targetIndex = source.FindIndex(item =>
                string.Equals(
                    (item?.ApiName ?? string.Empty).Trim(),
                    targetApiName.Trim(),
                    StringComparison.OrdinalIgnoreCase));
            return TryMoveItems(source, ResolveSelectedIndexes(source, draggedApiNames), targetIndex, insertAfterTarget);
        }

        public bool MoveItemsToEndByApiName(IReadOnlyList<string> draggedApiNames)
        {
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
            var normalizedApiNames = AchievementOrderHelper.NormalizeApiNames(draggedApiNames);
            if (normalizedApiNames.Count == 0)
            {
                return new List<int>();
            }

            var selectedApiNameSet = new HashSet<string>(normalizedApiNames, StringComparer.OrdinalIgnoreCase);
            var indexes = new List<int>();
            for (var i = 0; i < source.Count; i++)
            {
                var apiName = (source[i]?.ApiName ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(apiName) && selectedApiNameSet.Contains(apiName))
                {
                    indexes.Add(i);
                }
            }

            return indexes;
        }

        /// <summary>
        /// Persists the current row order. Called after a drag, so the list already reflects the
        /// user's intent; custom achievements take part exactly like provider ones.
        /// </summary>
        public void PersistCurrentOrder()
        {
            var ordered = AchievementRows
                .Where(row => row != null && !string.IsNullOrWhiteSpace(row.ApiName))
                .Select(row => row.ApiName)
                .ToList();
            _achievementOverridesService.SetAchievementOrderOverride(_gameId, ordered);
            RaisePersisted();
        }

        private void ResetCustomOrder()
        {
            _achievementOverridesService.SetAchievementOrderOverride(_gameId, Array.Empty<string>());
            RaisePersisted();
            ReloadData();
        }

        /// <summary>
        /// Applies one facet to every selected row in a single store write. Each setter rewrites a
        /// whole map or list and raises a change notification, so looping per row would pay that
        /// cost once per achievement instead of once per action.
        /// </summary>
        /// <remarks>
        /// Capstone is deliberately not a bulk action: a game has at most one, so applying it to a
        /// selection has no meaning. It stays a single-row edit.
        /// </remarks>
        public void ApplyBulkCategory(IReadOnlyList<AchievementEditorRow> selection, string category)
        {
            ApplyBulk(selection, row => row.EditCategory = category, () =>
                _achievementOverridesService.SetAchievementCategoryOverrides(
                    _gameId,
                    BuildFacetMap(r => r.EditCategory)));
        }

        public void ApplyBulkCategoryType(IReadOnlyList<AchievementEditorRow> selection, string categoryType)
        {
            ApplyBulk(selection, row => row.EditCategoryType = categoryType, () =>
                _achievementOverridesService.SetAchievementCategoryTypeOverrides(
                    _gameId,
                    BuildFacetMap(r => r.EditCategoryType)));
        }

        public void ApplyBulkFiltered(IReadOnlyList<AchievementEditorRow> selection, bool isFiltered)
        {
            ApplyBulk(selection, row => row.EditIsFiltered = isFiltered, PersistFilters);
        }

        public void ApplyBulkSummaryFiltered(IReadOnlyList<AchievementEditorRow> selection, bool isSummaryFiltered)
        {
            ApplyBulk(selection, row => row.EditIsSummaryFiltered = isSummaryFiltered, PersistFilters);
        }

        public void ApplyBulkTrophyType(IReadOnlyList<AchievementEditorRow> selection, string trophyType)
        {
            ApplyBulkPerRow(selection, row => row.EditTrophyType = trophyType, AchievementEditorField.TrophyType);
        }

        public void ApplyBulkPoints(IReadOnlyList<AchievementEditorRow> selection, int? points)
        {
            var text = points?.ToString(System.Globalization.CultureInfo.CurrentCulture);
            ApplyBulkPerRow(selection, row => row.EditPointsText = text, AchievementEditorField.Points);
        }

        public void ApplyBulkGoal(IReadOnlyList<AchievementEditorRow> selection, bool isGoal)
        {
            // Goals are stored as one ordered list, so the whole set is rewritten once.
            ApplyBulk(selection, row => row.EditIsGoal = isGoal, () =>
            {
                var goals = _allRows
                    .Where(row => row != null && row.EditIsGoal && !string.IsNullOrWhiteSpace(row.ApiName))
                    .Select(row => row.ApiName)
                    .ToList();
                _achievementOverridesService.SetGoalAchievements(_gameId, goals);
            });
        }

        /// <summary>
        /// Stages a facet across the selection without persisting per row, then writes once.
        /// </summary>
        private void ApplyBulk(
            IReadOnlyList<AchievementEditorRow> selection,
            Action<AchievementEditorRow> stage,
            Action persist)
        {
            if (selection == null || selection.Count == 0)
            {
                return;
            }

            foreach (var row in selection)
            {
                row?.SuppressFieldEdits(() => stage(row));
            }

            persist();
            RaisePersisted();
        }

        /// <summary>
        /// For facets stored per achievement rather than as one collection, so each row is written
        /// on its own. Used for the provider field overrides and the authored definitions.
        /// </summary>
        private void ApplyBulkPerRow(
            IReadOnlyList<AchievementEditorRow> selection,
            Action<AchievementEditorRow> stage,
            AchievementEditorField field)
        {
            if (selection == null || selection.Count == 0)
            {
                return;
            }

            foreach (var row in selection)
            {
                if (row == null)
                {
                    continue;
                }

                row.SuppressFieldEdits(() => stage(row));
                if (row.IsCustomRow)
                {
                    PersistCustomRowField(row, field);
                }
                else
                {
                    PersistProviderRowField(row, field);
                }
            }
        }

        private void SetStatus(string message, bool isError)
        {
            StatusMessage = message;
            StatusIsError = isError;
        }

        private void RaisePersisted()
        {
            CustomizationPersisted?.Invoke(this, EventArgs.Empty);
        }
    }
}
