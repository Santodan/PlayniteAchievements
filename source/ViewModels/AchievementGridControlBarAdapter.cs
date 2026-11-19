using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Services.Search;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels
{
    /// <summary>What the rows a control bar filters contain, for the options it offers.</summary>
    internal sealed class AchievementBarOptions
    {
        public static readonly AchievementBarOptions None = new AchievementBarOptions();

        public bool HasUnlocked { get; set; }

        public bool HasLocked { get; set; }

        public bool HasHiddenLocked { get; set; }

        public bool HasTrophies { get; set; }

        /// <summary>The one game every row belongs to; null for rows spanning games.</summary>
        public Guid? GameId { get; set; }

        /// <summary>The stored tag texts present on the rows.</summary>
        public IReadOnlyList<string> Tags { get; set; }

        /// <summary>The category labels in filter order.</summary>
        public IReadOnlyList<string> CategoryLabels { get; set; }
    }

    public sealed class AchievementGridControlBarAdapter : PlayniteAchievements.Common.ObservableObject
    {
        private readonly SearchTextIndex<AchievementDisplayItem> _searchIndex =
            new SearchTextIndex<AchievementDisplayItem>(item =>
                SearchTextBuilder.ForAchievement(item?.DisplayName, item?.Description));
        private readonly HashSet<string> _selectedCategoryTypeFilters =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _selectedCategoryLabelFilters =
            new HashSet<string>(CategoryPathHelper.LabelComparer);
        private string _searchText = string.Empty;
        private bool _showUnlocked = true;
        private bool _showLocked = true;
        private bool _showHidden = true;
        private bool _hasUnlocked;
        private bool _hasLocked;
        private bool _hasHiddenLocked;
        private Action<string> _saveUnlockToggles;
        private IGridCompareSource _friendCompare;
        private GridMultiSelectFilter _friendCompareFilter;
        private readonly RarityTrophyControlBarFilters _tierFilters;

        public AchievementGridControlBarAdapter()
        {
            CategoryTypeFilterOptions = new ObservableCollection<string>();
            CategoryLabelFilterOptions = new ObservableCollection<string>();
            _tierFilters = new RarityTrophyControlBarFilters(RaiseFilterChanged);
            ControlBar = CreateControlBar();
        }

        public event EventHandler FilterChanged;

        public GridControlBarViewModel ControlBar { get; }

        /// <summary>
        /// The bar's Rarity (the tiers and Capstone) and Trophy filters, the widget bars' own.
        /// Trophy is offered only while some row of the last <see cref="UpdateOptions"/> has a
        /// trophy type.
        /// </summary>
        public RarityTrophyControlBarFilters TierFilters => _tierFilters;

        /// <summary>
        /// Wires a compare source into the bar's pre-built Compare dropdown (the item just ahead
        /// of the unlock-state toggles). The slot exists on every adapter bar and stays hidden
        /// until a source is attached and reports itself available. Accepts either compare flavor:
        /// friend-onto-self (<see cref="FriendCompareController"/>) or friend-onto-friend
        /// (<see cref="FriendVsFriendCompareController"/>).
        /// </summary>
        public void AttachFriendCompare(IGridCompareSource source)
        {
            if (source == null || _friendCompare != null)
            {
                return;
            }

            _friendCompare = source;
            source.PropertyChanged += (_, __) => _friendCompareFilter?.Refresh();
            _friendCompareFilter?.Refresh();
        }

        public string SearchText
        {
            get => _searchText;
            set
            {
                var normalized = value ?? string.Empty;
                if (string.Equals(_searchText, normalized, StringComparison.Ordinal))
                {
                    return;
                }

                _searchText = normalized;
                OnPropertyChanged(nameof(SearchText));
                RaiseFilterChanged();
            }
        }

        public bool ShowUnlocked
        {
            get => _showUnlocked;
            set
            {
                if (_showUnlocked == value)
                {
                    return;
                }

                _showUnlocked = value;
                OnPropertyChanged(nameof(ShowUnlocked));
                SaveUnlockToggles();
                RaiseFilterChanged();
            }
        }

        public bool ShowLocked
        {
            get => _showLocked;
            set
            {
                if (_showLocked == value)
                {
                    return;
                }

                _showLocked = value;
                OnPropertyChanged(nameof(ShowLocked));
                SaveUnlockToggles();
                RaiseFilterChanged();
            }
        }

        public bool ShowHidden
        {
            get => _showHidden;
            set
            {
                if (_showHidden == value)
                {
                    return;
                }

                _showHidden = value;
                OnPropertyChanged(nameof(ShowHidden));
                SaveUnlockToggles();
                RaiseFilterChanged();
            }
        }

        // A toggle filters rows only while its button is offered, so a value left off on one
        // grid does not empty a grid that has no button to turn it back on.
        private bool UnlockStateTogglesOffered => _hasUnlocked && _hasLocked;

        private bool EffectiveShowUnlocked => _showUnlocked || !UnlockStateTogglesOffered;

        private bool EffectiveShowLocked => _showLocked || !UnlockStateTogglesOffered;

        private bool EffectiveShowHidden => _showHidden || !_hasHiddenLocked;

        /// <summary>
        /// Restores the Unlocked, Locked and Hidden toggles from <paramref name="saved"/> (a
        /// <see cref="GridControlBarSurfaceState"/> string, or null) and hands <paramref name="save"/>
        /// the new state on every later change made through the bar; a null state means every
        /// toggle is on. Applies silently; the host runs its filter pipeline afterwards.
        /// </summary>
        public void PersistUnlockToggles(string saved, Action<string> save)
        {
            _saveUnlockToggles = save;
            var state = GridControlBarSurfaceState.Deserialize(saved);
            _showUnlocked = state?.ShowUnlocked != false;
            _showLocked = state?.ShowLocked != false;
            _showHidden = state?.ShowHidden != false;
            OnPropertyChanged(nameof(ShowUnlocked));
            OnPropertyChanged(nameof(ShowLocked));
            OnPropertyChanged(nameof(ShowHidden));
        }

        private void SaveUnlockToggles()
        {
            _saveUnlockToggles?.Invoke(GridControlBarSurfaceState.Serialize(
                GridControlBarSurfaceState.CaptureUnlockToggles(_showUnlocked, _showLocked, _showHidden)));
        }

        public ObservableCollection<string> CategoryTypeFilterOptions { get; }

        public string SelectedCategoryTypeFilterText => GetSelectedFilterText(
            _selectedCategoryTypeFilters,
            CategoryTypeFilterOptions,
            L("LOCPlayAch_Common_Tags"),
            GetTypeDisplayName);

        // The game every listed row belongs to, so a game's own tags show their names; null for a
        // list spanning games, where only global tags resolve.
        private Guid? _optionsGameId;

        private string GetTypeDisplayName(string key) =>
            AchievementClassificationFilter.GetDisplayName(key, _optionsGameId);

        public ObservableCollection<string> CategoryLabelFilterOptions { get; }

        public string SelectedCategoryLabelFilterText => GetSelectedFilterText(
            _selectedCategoryLabelFilters,
            CategoryLabelFilterOptions,
            L("LOCPlayAch_Common_Category"),
            AchievementCategoryHelper.ToCategoryLabelDisplayText);

        public IReadOnlyList<AchievementDisplayItem> Apply(IEnumerable<AchievementDisplayItem> source)
        {
            var items = (source ?? Enumerable.Empty<AchievementDisplayItem>())
                .Where(item => item != null)
                .ToList();

            IEnumerable<AchievementDisplayItem> filtered = items;
            var searchQuery = SearchQuery.From(SearchText);

            // Only index while a search is active: SearchTextIndex.Matches lazily fills missing
            // entries, and hosts that keep their own search pipeline (adapter SearchText empty)
            // never pay for indexing the source rows.
            if (searchQuery.HasValue)
            {
                _searchIndex.Rebuild(items);
            }

            if (!EffectiveShowHidden)
            {
                filtered = filtered.Where(item => !(item.Hidden && !item.Unlocked));
            }

            var showUnlocked = EffectiveShowUnlocked;
            var showLocked = EffectiveShowLocked;
            filtered = filtered.Where(item => item.Unlocked ? showUnlocked : showLocked);

            if (_selectedCategoryTypeFilters.Count > 0)
            {
                var selectedTypes = _selectedCategoryTypeFilters.ToList();
                filtered = filtered.Where(item => selectedTypes.Any(key =>
                    AchievementClassificationFilter.Matches(item.CategoryKind, item.Tags, key)));
            }

            if (_selectedCategoryLabelFilters.Count > 0)
            {
                var selectedCategorySet = new HashSet<string>(
                    _selectedCategoryLabelFilters
                        .Select(AchievementCategoryHelper.NormalizeCategoryOrDefault)
                        .Where(value => !string.IsNullOrWhiteSpace(value)),
                    CategoryPathHelper.LabelComparer);
                filtered = filtered.Where(item =>
                    selectedCategorySet.Contains(
                        AchievementCategoryHelper.NormalizeCategoryOrDefault(item.CategoryLabel)));
            }

            if (_tierFilters.HasSelection)
            {
                filtered = filtered.Where(_tierFilters.Matches);
            }

            if (searchQuery.HasValue)
            {
                filtered = filtered.Where(item => _searchIndex.Matches(item, searchQuery));
            }

            return filtered.ToList();
        }

        public void UpdateOptions(IEnumerable<AchievementDisplayItem> source)
        {
            var sourceItems = (source ?? Enumerable.Empty<AchievementDisplayItem>())
                .Where(item => item != null)
                .ToList();
            var gameIds = sourceItems.Select(item => item.PlayniteGameId).Distinct().ToList();
            UpdateOptions(new AchievementBarOptions
            {
                HasUnlocked = sourceItems.Any(item => item.Unlocked),
                HasLocked = sourceItems.Any(item => !item.Unlocked),
                HasHiddenLocked = sourceItems.Any(item => item.Hidden && !item.Unlocked),
                HasTrophies = sourceItems.Any(row => !string.IsNullOrWhiteSpace(row.TrophyType)),
                GameId = gameIds.Count == 1 ? gameIds[0] : null,
                Tags = sourceItems.Select(item => item.Tags).ToList(),
                CategoryLabels = AchievementCategoryFilterOrderHelper.BuildOrderedCategoryLabels(
                    sourceItems,
                    item => item?.CategoryLabel,
                    sourceItems
                        .Where(item => item != null && item.CategoryOrderIndex < int.MaxValue)
                        .OrderBy(item => item.CategoryOrderIndex)
                        .Select(item => item.CategoryLabel))
            });
        }

        /// <summary>Offers the bar's options for rows described by <paramref name="options"/>; null offers none.</summary>
        internal void UpdateOptions(AchievementBarOptions options)
        {
            options = options ?? AchievementBarOptions.None;
            _hasUnlocked = options.HasUnlocked;
            _hasLocked = options.HasLocked;
            _hasHiddenLocked = options.HasHiddenLocked;
            _optionsGameId = options.GameId;
            var typeOptions = AchievementClassificationFilter.GetPresentKeys(
                options.Tags ?? Array.Empty<string>(),
                tags => tags,
                _optionsGameId);
            var categoryOptions = options.CategoryLabels ?? Array.Empty<string>();

            CollectionHelper.SynchronizeCollection(CategoryTypeFilterOptions, typeOptions);
            CollectionHelper.SynchronizeCollection(CategoryLabelFilterOptions, categoryOptions);

            if (PruneFilterSelections(_selectedCategoryTypeFilters, CategoryTypeFilterOptions))
            {
                OnPropertyChanged(nameof(SelectedCategoryTypeFilterText));
            }

            if (PruneFilterSelections(_selectedCategoryLabelFilters, CategoryLabelFilterOptions))
            {
                OnPropertyChanged(nameof(SelectedCategoryLabelFilterText));
            }

            _tierFilters.OfferTrophies(options.HasTrophies);
            ControlBar.Refresh();
        }

        /// <summary>
        /// <paramref name="query"/> narrowed to the rows the bar keeps: its unlock-state toggles
        /// while they are offered, Tags, Category, Rarity and Trophy picks, and its own search
        /// text. A rows filter that is not offered keeps every row, as <see cref="Apply"/> does.
        /// </summary>
        internal AchievementQuery ApplyTo(AchievementQuery query)
        {
            if (!EffectiveShowHidden)
            {
                query = query.WithoutHiddenLocked();
            }

            var showUnlocked = EffectiveShowUnlocked;
            var showLocked = EffectiveShowLocked;
            if (!showUnlocked || !showLocked)
            {
                var keep = showUnlocked ? true : showLocked ? false : (bool?)null;
                query = keep.HasValue && (!query.Unlocked.HasValue || query.Unlocked == keep)
                    ? query.WhereUnlocked(keep)
                    : query.MatchNothing();
            }

            if (_selectedCategoryTypeFilters.Count > 0)
            {
                query = query.WhereClassification(_selectedCategoryTypeFilters.ToList());
            }

            if (_selectedCategoryLabelFilters.Count > 0)
            {
                query = query.WhereCategory(_selectedCategoryLabelFilters.ToList());
            }

            query = _tierFilters.ApplyTo(query);
            return SearchQuery.From(SearchText).HasValue ? query.WhereSearch(SearchText) : query;
        }

        public void Clear()
        {
            _searchIndex.Clear();
            UpdateOptions(AchievementBarOptions.None);
        }

        // True when any filter deviates from "show everything" (used for header/count logic).
        public bool HasActiveFilters =>
            !string.IsNullOrEmpty(_searchText)
            || !EffectiveShowUnlocked
            || !EffectiveShowLocked
            || !EffectiveShowHidden
            || _selectedCategoryTypeFilters.Count > 0
            || _selectedCategoryLabelFilters.Count > 0
            || _tierFilters.HasSelection;

        // Snapshot the current filter state (search, toggles, category, rarity and trophy selections).
        public GridControlBarFilterState CaptureState()
        {
            var state = new GridControlBarFilterState
            {
                SearchText = _searchText,
                ShowUnlocked = _showUnlocked,
                ShowLocked = _showLocked,
                ShowHidden = _showHidden,
                CategoryTypeFilters = _selectedCategoryTypeFilters.ToList(),
                CategoryLabelFilters = _selectedCategoryLabelFilters.ToList(),
                Tiers = new ControlBarFilterState()
            };
            _tierFilters.Capture(state.Tiers);
            return state;
        }

        // Restore a previously captured state. Applies silently by default so callers can
        // re-run their filter/sort pipeline once afterwards; set raiseChanged to fire
        // FilterChanged for the event-driven consumers.
        public void RestoreState(GridControlBarFilterState state, bool raiseChanged = false)
        {
            if (state == null)
            {
                return;
            }

            _searchText = state.SearchText ?? string.Empty;
            _showUnlocked = state.ShowUnlocked;
            _showLocked = state.ShowLocked;
            _showHidden = state.ShowHidden;

            ReplaceSelections(_selectedCategoryTypeFilters, state.CategoryTypeFilters);
            ReplaceSelections(_selectedCategoryLabelFilters, state.CategoryLabelFilters);
            _tierFilters.Restore(state.Tiers);

            RaiseControlBarChanged();
            if (raiseChanged)
            {
                RaiseFilterChanged();
            }
        }

        // Reset visibility toggles and category, rarity and trophy selections to "show
        // everything". Leaves the search text untouched (callers that share a search box reset
        // it explicitly).
        public void ResetFilters(bool raiseChanged = false)
        {
            _showUnlocked = true;
            _showLocked = true;
            _showHidden = true;
            ResetSelections(raiseChanged);
        }

        // Reset the tag, category, rarity and trophy selections, which belong to the rows of one
        // game. Leaves the unlocked/locked/hidden toggles and the search text untouched.
        public void ResetSelections(bool raiseChanged = false)
        {
            _selectedCategoryTypeFilters.Clear();
            _selectedCategoryLabelFilters.Clear();
            _tierFilters.Restore(null);

            RaiseControlBarChanged();
            if (raiseChanged)
            {
                RaiseFilterChanged();
            }
        }

        private static void ReplaceSelections(HashSet<string> target, IEnumerable<string> values)
        {
            target.Clear();
            if (values == null)
            {
                return;
            }

            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    target.Add(value.Trim());
                }
            }
        }

        // Refresh every bound control-bar item after a bulk state change (no FilterChanged).
        private void RaiseControlBarChanged()
        {
            OnPropertyChanged(nameof(SearchText));
            OnPropertyChanged(nameof(ShowUnlocked));
            OnPropertyChanged(nameof(ShowLocked));
            OnPropertyChanged(nameof(ShowHidden));
            OnPropertyChanged(nameof(SelectedCategoryTypeFilterText));
            OnPropertyChanged(nameof(SelectedCategoryLabelFilterText));
        }

        private GridControlBarViewModel CreateControlBar()
        {
            var controlBar = new GridControlBarViewModel
            {
                Search = new GridSearchControl(
                    this,
                    nameof(SearchText),
                    () => SearchText,
                    value => SearchText = value,
                    L("LOCPlayAch_Filter_Achievements"),
                    () => SearchText = string.Empty)
                {
                    Id = ShowcaseControlBarItems.Search,
                    LabelKey = "LOCPlayAch_Filter_Achievements"
                }
            };
            controlBar.Items.Add(new GridMultiSelectFilter(
                this,
                nameof(SelectedCategoryTypeFilterText),
                () => SelectedCategoryTypeFilterText,
                () => CategoryTypeFilterOptions,
                IsCategoryTypeFilterSelected,
                SetCategoryTypeFilterSelected,
                GetTypeDisplayName)
            {
                Id = ShowcaseControlBarItems.Tag,
                LabelKey = "LOCPlayAch_Common_Tags",
                Width = 120,
                // A tag's icon; the other classification keys have none, and name themselves.
                IconKeyOf = key => TagControlBarFilter.IconKey(key, _optionsGameId),
                IsCategoryFilter = true,
                ToolTip = L("LOCPlayAch_ManageAchievements_Category_Filter_Tags")
            });
            controlBar.Items.Add(new GridMultiSelectFilter(
                this,
                nameof(SelectedCategoryLabelFilterText),
                () => SelectedCategoryLabelFilterText,
                () => CategoryLabelFilterOptions,
                IsCategoryLabelFilterSelected,
                SetCategoryLabelFilterSelected,
                AchievementCategoryHelper.ToCategoryLabelDisplayText)
            {
                Id = ShowcaseControlBarItems.Category,
                LabelKey = "LOCPlayAch_Common_Category",
                Width = 140,
                IsCategoryFilter = true,
                RendersCategoryTree = true,
                ToolTip = L("LOCPlayAch_ManageAchievements_Category_Filter_Label")
            });
            controlBar.Items.Add(_tierFilters.RarityItem);
            controlBar.Items.Add(_tierFilters.TrophyItem);
            _friendCompareFilter = new GridMultiSelectFilter(
                this,
                null,
                () => _friendCompare?.CompareSelectionText ?? L("LOCPlayAch_Filter_CompareSelectorPlaceholder"),
                () => _friendCompare?.OptionKeys ?? Enumerable.Empty<string>(),
                key => _friendCompare?.IsKeySelected(key) == true,
                (key, isSelected) => _friendCompare?.SelectKey(key, isSelected),
                key => _friendCompare?.GetDisplayNameForKey(key) ?? key,
                () => _friendCompare?.IsCompareAvailable == true,
                key => _friendCompare?.IsKeyFavorite(key) == true)
            {
                Id = ShowcaseControlBarItems.Compare,
                LabelKey = "LOCPlayAch_Filter_CompareSelectorPlaceholder",
                Width = 140,
                ToolTip = L("LOCPlayAch_Filter_CompareSelectorPlaceholder"),
                StaysOnBar = true,
                IsMutedOption = key => _friendCompare?.IsKeyMuted(key) == true
            };
            controlBar.Items.Add(_friendCompareFilter);
            controlBar.Items.Add(new GridToggleFilter(
                this,
                nameof(ShowUnlocked),
                L("LOCPlayAch_Common_Unlocked"),
                () => ShowUnlocked,
                value => ShowUnlocked = value,
                GridToggleFilterIcon.Unlocked,
                () => _hasUnlocked && _hasLocked)
            {
                Id = ShowcaseControlBarItems.Unlocked,
                LabelKey = "LOCPlayAch_Common_Unlocked",
                ClearedValue = true
            });
            controlBar.Items.Add(new GridToggleFilter(
                this,
                nameof(ShowLocked),
                L("LOCPlayAch_Common_Locked"),
                () => ShowLocked,
                value => ShowLocked = value,
                GridToggleFilterIcon.Locked,
                () => _hasUnlocked && _hasLocked)
            {
                Id = ShowcaseControlBarItems.Locked,
                LabelKey = "LOCPlayAch_Common_Locked",
                ClearedValue = true
            });
            controlBar.Items.Add(new GridToggleFilter(
                this,
                nameof(ShowHidden),
                L("LOCPlayAch_Filter_Hidden"),
                () => ShowHidden,
                value => ShowHidden = value,
                GridToggleFilterIcon.Hidden,
                () => _hasHiddenLocked)
            {
                Id = ShowcaseControlBarItems.Hidden,
                LabelKey = "LOCPlayAch_Filter_Hidden",
                ClearedValue = true
            });
            return controlBar;
        }

        public bool IsCategoryTypeFilterSelected(string value)
        {
            return IsFilterSelected(_selectedCategoryTypeFilters, value);
        }

        /// <summary>
        /// Picks or drops a kind or tag in the Tags filter; the overview's tag pie sets it for the
        /// selected game's grid.
        /// </summary>
        public void SetCategoryTypeFilterSelected(string value, bool isSelected)
        {
            if (!SetFilterSelection(_selectedCategoryTypeFilters, value, isSelected))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedCategoryTypeFilterText));
            RaiseFilterChanged();
        }

        private bool IsCategoryLabelFilterSelected(string value)
        {
            return IsFilterSelected(_selectedCategoryLabelFilters, value);
        }

        private void SetCategoryLabelFilterSelected(string value, bool isSelected)
        {
            if (!SetFilterSelection(_selectedCategoryLabelFilters, value, isSelected))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedCategoryLabelFilterText));
            RaiseFilterChanged();
        }

        private void RaiseFilterChanged()
        {
            FilterChanged?.Invoke(this, EventArgs.Empty);
        }

        private static bool IsFilterSelected(HashSet<string> selectedValues, string value)
        {
            if (selectedValues == null || string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return selectedValues.Contains(value.Trim());
        }

        private static bool SetFilterSelection(HashSet<string> selectedValues, string value, bool isSelected)
        {
            if (selectedValues == null || string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var normalized = value.Trim();
            return isSelected
                ? selectedValues.Add(normalized)
                : selectedValues.Remove(normalized);
        }

        private static bool PruneFilterSelections(HashSet<string> selectedValues, IEnumerable<string> options)
        {
            if (selectedValues == null)
            {
                return false;
            }

            // Options match the selection's own identity rule: type keys ignore case, category labels do not.
            var optionSet = new HashSet<string>(
                (options ?? Enumerable.Empty<string>()).Where(value => !string.IsNullOrWhiteSpace(value)),
                selectedValues.Comparer);
            return selectedValues.RemoveWhere(value => !optionSet.Contains(value)) > 0;
        }

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

            var ordered = new List<string>();
            foreach (var option in options ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(option) && selectedValues.Contains(option))
                {
                    ordered.Add(option);
                }
            }

            if (ordered.Count == 0)
            {
                ordered.AddRange(selectedValues.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
            }

            return string.Join(", ", ordered.Select(value => displayText?.Invoke(value) ?? value));
        }

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
        }
    }
}
