using System;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Search;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    // Narrowing the category grid: a search box over the names, a category dropdown that keeps a
    // node with its subcategories, and a type dropdown. The filter drives the collection view
    // only; CategoryRows stays whole, since every persist path walks it as the complete order.
    public sealed partial class ManageAchievementsCategoryViewModel
    {
        private string _filterText;
        private SearchQuery _filterQuery = SearchQuery.From(null);
        private readonly HashSet<string> _selectedCategoryFilters = new HashSet<string>(CategoryPathHelper.LabelComparer);
        private readonly HashSet<string> _selectedKindFilters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private GridMultiSelectFilter _categoryFilter;
        private GridMultiSelectFilter _kindFilter;

        /// <summary>Narrows the grid to categories whose name, path or provider name contains this text.</summary>
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

        public GridMultiSelectFilter CategoryFilter => _categoryFilter ?? (_categoryFilter = BuildCategoryFilter());

        public GridMultiSelectFilter KindFilter => _kindFilter ?? (_kindFilter = BuildKindFilter());

        /// <summary>True while the grid shows a subset of the categories.</summary>
        public bool IsFiltering =>
            _filterQuery.HasValue ||
            _selectedCategoryFilters.Count > 0 ||
            _selectedKindFilters.Count > 0;

        /// <summary>
        /// Drag, indent and outdent resolve their destination against the neighbouring rows, and a
        /// filtered grid hides those, so the gestures wait until the filter is cleared.
        /// </summary>
        public bool CanReorder => !IsFiltering;

        /// <summary>Raised when the filter changed and the collection view needs refreshing.</summary>
        public event EventHandler FilterChanged;

        /// <summary>
        /// Raised when the option sets behind the filters change, which is what the drop-downs
        /// subscribe to rather than each holding a collection of its own.
        /// </summary>
        public object FilterOptionsChanged => null;

        public bool MatchesFilter(ManageAchievementsCategoryMetadataItem row)
        {
            if (row == null)
            {
                return false;
            }

            if (_filterQuery.HasValue &&
                !_filterQuery.Matches(row.CategoryLeafDisplay) &&
                !_filterQuery.Matches(row.CategoryPathDisplay) &&
                !_filterQuery.Matches(row.ProviderCategoryLeafDisplay))
            {
                return false;
            }

            // A picked category keeps its subcategories: the subtree is what the pick names.
            if (_selectedCategoryFilters.Count > 0 &&
                !_selectedCategoryFilters.Any(selected =>
                    CategoryPathHelper.IsSelfOrDescendantOf(row.CategoryLabel, selected)))
            {
                return false;
            }

            if (_selectedKindFilters.Count > 0 && !MatchesSelectedKinds(row))
            {
                return false;
            }

            return true;
        }

        // A category can carry several kinds at once ("DLC|Update"), so it passes when any of
        // its own is ticked. None matches a category with no kind, stored or resolved.
        private bool MatchesSelectedKinds(ManageAchievementsCategoryMetadataItem row)
        {
            var components = CategoryKindHelper.GetComponents(row.Kind);
            if (components.Count == 0)
            {
                return _selectedKindFilters.Contains(CategoryKindHelper.None);
            }

            return components.Any(_selectedKindFilters.Contains);
        }

        public void ClearFilters()
        {
            _selectedCategoryFilters.Clear();
            _selectedKindFilters.Clear();
            _filterQuery = SearchQuery.From(null);
            if (SetValueAndReturn(ref _filterText, null))
            {
                OnPropertyChanged(nameof(FilterText));
            }

            _categoryFilter?.Refresh();
            _kindFilter?.Refresh();
            NotifyFilterChanged();
        }

        private GridMultiSelectFilter BuildCategoryFilter()
        {
            return new GridMultiSelectFilter(
                this,
                nameof(FilterOptionsChanged),
                () => GetSelectedFilterText(
                    _selectedCategoryFilters,
                    CategoryFilterOptions,
                    ResourceProvider.GetString("LOCPlayAch_Common_Category"),
                    CategoryPathHelper.ToDisplayLeaf),
                () => CategoryFilterOptions,
                option => _selectedCategoryFilters.Contains(option),
                (option, isSelected) => ToggleFilter(_selectedCategoryFilters, option, isSelected),
                getDisplayLabel: CategoryPathHelper.GetLeafName)
            {
                RendersCategoryTree = true,
                MinWidth = 140
            };
        }

        private GridMultiSelectFilter BuildKindFilter()
        {
            var options = CategoryKindHelper.AssignableKinds
                .Concat(new[] { CategoryKindHelper.None })
                .ToList();
            return new GridMultiSelectFilter(
                this,
                nameof(FilterOptionsChanged),
                () => GetSelectedFilterText(
                    _selectedKindFilters,
                    options,
                    ResourceProvider.GetString("LOCPlayAch_Common_Type"),
                    GetKindFilterLabel),
                () => options,
                option => _selectedKindFilters.Contains(option),
                (option, isSelected) => ToggleFilter(_selectedKindFilters, option, isSelected),
                getDisplayLabel: GetKindFilterLabel)
            {
                MinWidth = 120
            };
        }

        private IEnumerable<string> CategoryFilterOptions =>
            CategoryRows
                .Where(row => row != null && !string.IsNullOrWhiteSpace(row.CategoryLabel))
                .Select(row => row.CategoryLabel);

        private static string GetKindFilterLabel(string kind)
        {
            return string.Equals(kind, CategoryKindHelper.None, StringComparison.OrdinalIgnoreCase)
                ? ResourceProvider.GetString("LOCPlayAch_Common_None")
                : CategoryKindHelper.ToKindDisplayText(kind);
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
            Func<string, string> displayText)
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

        private void NotifyFilterChanged()
        {
            OnPropertyChanged(nameof(IsFiltering));
            OnPropertyChanged(nameof(CanReorder));
            FilterChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// After a row rebuild: the category choices follow the rows, and ticks on labels the game
        /// no longer has are dropped rather than clearing the whole selection on every edit.
        /// </summary>
        private void RefreshFilterOptions()
        {
            var removed = _selectedCategoryFilters
                .Where(selected => !CategoryFilterOptions.Contains(selected, CategoryPathHelper.LabelComparer))
                .ToList();
            foreach (var stale in removed)
            {
                _selectedCategoryFilters.Remove(stale);
            }

            OnPropertyChanged(nameof(FilterOptionsChanged));
            _categoryFilter?.Refresh();
            _kindFilter?.Refresh();
            if (removed.Count > 0)
            {
                NotifyFilterChanged();
            }
        }
    }
}
