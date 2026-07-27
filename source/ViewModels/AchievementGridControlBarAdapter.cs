using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Search;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels
{
    public sealed class AchievementGridControlBarAdapter : PlayniteAchievements.Common.ObservableObject
    {
        private readonly SearchTextIndex<AchievementDisplayItem> _searchIndex =
            new SearchTextIndex<AchievementDisplayItem>(item =>
                SearchTextBuilder.ForAchievement(item?.DisplayName, item?.Description));
        private readonly HashSet<string> _selectedCategoryTypeFilters =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _selectedCategoryLabelFilters =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string _searchText = string.Empty;
        private bool _showUnlocked = true;
        private bool _showLocked = true;
        private bool _showHidden = true;
        private bool _hasUnlocked;
        private bool _hasLocked;
        private bool _hasHiddenLocked;

        public AchievementGridControlBarAdapter()
        {
            CategoryTypeFilterOptions = new ObservableCollection<string>();
            CategoryLabelFilterOptions = new ObservableCollection<string>();
            ControlBar = CreateControlBar();
        }

        public event EventHandler FilterChanged;

        public GridControlBarViewModel ControlBar { get; }

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
                RaiseFilterChanged();
            }
        }

        public ObservableCollection<string> CategoryTypeFilterOptions { get; }

        public string SelectedCategoryTypeFilterText => GetSelectedFilterText(
            _selectedCategoryTypeFilters,
            CategoryTypeFilterOptions,
            L("LOCPlayAch_Common_Label_Type"),
            AchievementCategoryTypeHelper.ToCategoryTypeDisplayText);

        public ObservableCollection<string> CategoryLabelFilterOptions { get; }

        public string SelectedCategoryLabelFilterText => GetSelectedFilterText(
            _selectedCategoryLabelFilters,
            CategoryLabelFilterOptions,
            L("LOCPlayAch_Common_Label_Category"),
            AchievementCategoryTypeHelper.ToCategoryLabelDisplayText);

        public IReadOnlyList<AchievementDisplayItem> Apply(IEnumerable<AchievementDisplayItem> source)
        {
            var items = (source ?? Enumerable.Empty<AchievementDisplayItem>())
                .Where(item => item != null)
                .ToList();

            _searchIndex.Rebuild(items);
            IEnumerable<AchievementDisplayItem> filtered = items;
            var searchQuery = SearchQuery.From(SearchText);

            if (!ShowHidden)
            {
                filtered = filtered.Where(item => !(item.Hidden && !item.Unlocked));
            }

            filtered = filtered.Where(item => item.Unlocked ? ShowUnlocked : ShowLocked);

            if (_selectedCategoryTypeFilters.Count > 0)
            {
                var selectedTypeSet = new HashSet<string>(
                    _selectedCategoryTypeFilters
                        .Select(AchievementCategoryTypeHelper.NormalizeOrDefault)
                        .Where(value => !string.IsNullOrWhiteSpace(value)),
                    StringComparer.OrdinalIgnoreCase);
                filtered = filtered.Where(item =>
                    AchievementCategoryTypeHelper.ParseValues(
                            AchievementCategoryTypeHelper.NormalizeOrDefault(item.CategoryType))
                        .Any(selectedTypeSet.Contains));
            }

            if (_selectedCategoryLabelFilters.Count > 0)
            {
                var selectedCategorySet = new HashSet<string>(
                    _selectedCategoryLabelFilters
                        .Select(AchievementCategoryTypeHelper.NormalizeCategoryOrDefault)
                        .Where(value => !string.IsNullOrWhiteSpace(value)),
                    StringComparer.OrdinalIgnoreCase);
                filtered = filtered.Where(item =>
                    selectedCategorySet.Contains(
                        AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(item.CategoryLabel)));
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
            var typeValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _hasUnlocked = sourceItems.Any(item => item.Unlocked);
            _hasLocked = sourceItems.Any(item => !item.Unlocked);
            _hasHiddenLocked = sourceItems.Any(item => item.Hidden && !item.Unlocked);

            if (sourceItems.Count > 0)
            {
                foreach (var item in sourceItems)
                {
                    var parsedTypes = AchievementCategoryTypeHelper.ParseValues(
                        AchievementCategoryTypeHelper.NormalizeOrDefault(item.CategoryType));
                    foreach (var parsedType in parsedTypes)
                    {
                        if (!string.IsNullOrWhiteSpace(parsedType))
                        {
                            typeValues.Add(parsedType);
                        }
                    }
                }
            }

            var typeOptions = AchievementCategoryTypeHelper.AllowedCategoryTypes
                .Where(typeValues.Contains)
                .ToList();
            var categoryOptions = AchievementCategoryFilterOrderHelper.BuildOrderedCategoryLabels(
                sourceItems,
                item => item?.CategoryLabel,
                sourceItems
                    .Where(item => item != null && item.CategoryOrderIndex < int.MaxValue)
                    .OrderBy(item => item.CategoryOrderIndex)
                    .Select(item => item.CategoryLabel));

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

            ControlBar.Refresh();
        }

        public void Clear()
        {
            _searchIndex.Clear();
            UpdateOptions(null);
        }

        // True when any filter deviates from "show everything" (used for header/count logic).
        public bool HasActiveFilters =>
            !string.IsNullOrEmpty(_searchText)
            || !_showUnlocked
            || !_showLocked
            || !_showHidden
            || _selectedCategoryTypeFilters.Count > 0
            || _selectedCategoryLabelFilters.Count > 0;

        // Snapshot the current filter state (search, toggles, category selections).
        public GridControlBarFilterState CaptureState()
        {
            return new GridControlBarFilterState
            {
                SearchText = _searchText,
                ShowUnlocked = _showUnlocked,
                ShowLocked = _showLocked,
                ShowHidden = _showHidden,
                CategoryTypeFilters = _selectedCategoryTypeFilters.ToList(),
                CategoryLabelFilters = _selectedCategoryLabelFilters.ToList(),
            };
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

            RaiseControlBarChanged();
            if (raiseChanged)
            {
                RaiseFilterChanged();
            }
        }

        // Reset visibility toggles and category selections to "show everything". Leaves the
        // search text untouched (callers that share a search box reset it explicitly).
        public void ResetFilters(bool raiseChanged = false)
        {
            _showUnlocked = true;
            _showLocked = true;
            _showHidden = true;
            _selectedCategoryTypeFilters.Clear();
            _selectedCategoryLabelFilters.Clear();

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
            };
            controlBar.Items.Add(new GridMultiSelectFilter(
                this,
                nameof(SelectedCategoryTypeFilterText),
                () => SelectedCategoryTypeFilterText,
                () => CategoryTypeFilterOptions,
                IsCategoryTypeFilterSelected,
                SetCategoryTypeFilterSelected,
                AchievementCategoryTypeHelper.ToCategoryTypeDisplayText)
            {
                Width = 120,
                ToolTip = L("LOCPlayAch_ManageAchievements_Category_Filter_Type")
            });
            controlBar.Items.Add(new GridMultiSelectFilter(
                this,
                nameof(SelectedCategoryLabelFilterText),
                () => SelectedCategoryLabelFilterText,
                () => CategoryLabelFilterOptions,
                IsCategoryLabelFilterSelected,
                SetCategoryLabelFilterSelected,
                AchievementCategoryTypeHelper.ToCategoryLabelDisplayText)
            {
                Width = 140,
                ToolTip = L("LOCPlayAch_ManageAchievements_Category_Filter_Label")
            });
            controlBar.Items.Add(new GridToggleFilter(
                this,
                nameof(ShowUnlocked),
                L("LOCPlayAch_Common_Unlocked"),
                () => ShowUnlocked,
                value => ShowUnlocked = value,
                GridToggleFilterIcon.Unlocked,
                () => _hasUnlocked && _hasLocked));
            controlBar.Items.Add(new GridToggleFilter(
                this,
                nameof(ShowLocked),
                L("LOCPlayAch_Common_Locked"),
                () => ShowLocked,
                value => ShowLocked = value,
                GridToggleFilterIcon.Locked,
                () => _hasUnlocked && _hasLocked));
            controlBar.Items.Add(new GridToggleFilter(
                this,
                nameof(ShowHidden),
                L("LOCPlayAch_Filter_Hidden"),
                () => ShowHidden,
                value => ShowHidden = value,
                GridToggleFilterIcon.Hidden,
                () => _hasHiddenLocked));
            return controlBar;
        }

        private bool IsCategoryTypeFilterSelected(string value)
        {
            return IsFilterSelected(_selectedCategoryTypeFilters, value);
        }

        private void SetCategoryTypeFilterSelected(string value, bool isSelected)
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

            var optionSet = new HashSet<string>(
                (options ?? Enumerable.Empty<string>()).Where(value => !string.IsNullOrWhiteSpace(value)),
                StringComparer.OrdinalIgnoreCase);
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
