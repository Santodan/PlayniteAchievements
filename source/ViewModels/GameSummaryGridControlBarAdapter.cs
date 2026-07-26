using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.Services.Search;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels
{
    public sealed class GameSummaryGridControlBarAdapter : PlayniteAchievements.Common.ObservableObject
    {
        private readonly SearchTextIndex<GameSummaryItem> _searchIndex =
            new SearchTextIndex<GameSummaryItem>(item =>
                SearchTextBuilder.ForGameSummary(item?.GameName));
        private readonly HashSet<string> _selectedProgressFilters =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _selectedActivityFilters =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string _searchText = string.Empty;
        private ObservableCollection<ProviderFilterGroup> _providerFilterGroups =
            new ObservableCollection<ProviderFilterGroup>();

        public GameSummaryGridControlBarAdapter()
        {
            ProgressFilterOptions = new ObservableCollection<string>
            {
                CompleteLabel,
                InProgressLabel,
                NoProgressLabel
            };
            ActivityFilterOptions = new ObservableCollection<string>
            {
                PlayedLabel,
                UnplayedLabel
            };
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

        public ObservableCollection<ProviderFilterGroup> ProviderFilterGroups
        {
            get => _providerFilterGroups;
            private set => SetValue(ref _providerFilterGroups, value ?? new ObservableCollection<ProviderFilterGroup>());
        }

        public string SelectedProviderFilterText =>
            OverviewGameSummaryFilters.BuildProviderFilterText(
                ProviderFilterGroups,
                L("LOCPlayAch_Common_Label_Platform"));

        public ObservableCollection<string> ProgressFilterOptions { get; }

        public string SelectedProgressFilterText => GetSelectedFilterText(
            _selectedProgressFilters,
            ProgressFilterOptions,
            L("LOCPlayAch_Progress"));

        public ObservableCollection<string> ActivityFilterOptions { get; }

        public string SelectedActivityFilterText => GetSelectedFilterText(
            _selectedActivityFilters,
            ActivityFilterOptions,
            L("LOCPlayAch_Filter_ActivitySelectorPlaceholder"));

        public IReadOnlyList<GameSummaryItem> Apply(IEnumerable<GameSummaryItem> source)
        {
            var items = (source ?? Enumerable.Empty<GameSummaryItem>())
                .Where(item => item != null)
                .ToList();

            _searchIndex.Rebuild(items);
            IEnumerable<GameSummaryItem> filtered = items;
            var searchQuery = SearchQuery.From(SearchText);
            if (searchQuery.HasValue)
            {
                filtered = filtered.Where(item => _searchIndex.Matches(item, searchQuery));
            }

            filtered = OverviewGameSummaryFilters.ApplyProviderPlatformFilter(filtered, ProviderFilterGroups);

            filtered = OverviewGameSummaryFilters.ApplyActivityAndProgressFilters(
                filtered,
                _selectedActivityFilters,
                _selectedProgressFilters,
                PlayedLabel,
                UnplayedLabel,
                CompleteLabel,
                InProgressLabel,
                NoProgressLabel);

            return filtered.ToList();
        }

        public void UpdateOptions(IEnumerable<GameSummaryItem> source)
        {
            var gameList = (source ?? Enumerable.Empty<GameSummaryItem>())
                .Where(item => item != null)
                .ToList();

            var priorSelections = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var priorExpanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var existing in ProviderFilterGroups ?? Enumerable.Empty<ProviderFilterGroup>())
            {
                var selected = existing.SelectedPlatformNames.ToList();
                if (selected.Count > 0)
                {
                    priorSelections[existing.ProviderKey] =
                        new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
                }

                if (existing.IsExpanded)
                {
                    priorExpanded.Add(existing.ProviderKey);
                }
            }

            var platformsByProvider = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var game in gameList)
            {
                var providerKey = game?.ProviderKey;
                if (string.IsNullOrWhiteSpace(providerKey))
                {
                    continue;
                }

                providerKey = providerKey.Trim();
                if (!platformsByProvider.TryGetValue(providerKey, out var platforms))
                {
                    platforms = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
                    platformsByProvider[providerKey] = platforms;
                }

                foreach (var platform in game.Platforms ?? Array.Empty<string>())
                {
                    if (!string.IsNullOrWhiteSpace(platform))
                    {
                        platforms.Add(platform.Trim());
                    }
                }
            }

            var groups = new List<ProviderFilterGroup>();
            foreach (var providerKey in platformsByProvider.Keys
                .OrderBy(GetProviderFilterDisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                var platformNames = platformsByProvider[providerKey].ToList();
                if (platformNames.Count == 0)
                {
                    platformNames.Add(GetProviderFilterDisplayName(providerKey));
                }

                priorSelections.TryGetValue(providerKey, out var selectedSet);
                groups.Add(new ProviderFilterGroup(
                    providerKey,
                    GetProviderFilterDisplayName(providerKey),
                    platformNames,
                    name => selectedSet != null && selectedSet.Contains(name),
                    OnProviderFilterSelectionChanged)
                {
                    IsExpanded = priorExpanded.Contains(providerKey)
                });
            }

            ProviderFilterGroups = new ObservableCollection<ProviderFilterGroup>(groups);
            OnPropertyChanged(nameof(SelectedProviderFilterText));
            ControlBar.Refresh();
        }

        public void Clear()
        {
            _searchIndex.Clear();
            UpdateOptions(null);
        }

        public bool IsProgressFilterSelected(string value)
        {
            return IsFilterSelected(_selectedProgressFilters, value);
        }

        public void SetProgressFilterSelected(string value, bool isSelected)
        {
            if (!SetFilterSelection(_selectedProgressFilters, value, isSelected))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedProgressFilterText));
            RaiseFilterChanged();
        }

        public bool IsActivityFilterSelected(string value)
        {
            return IsFilterSelected(_selectedActivityFilters, value);
        }

        public void SetActivityFilterSelected(string value, bool isSelected)
        {
            if (!SetFilterSelection(_selectedActivityFilters, value, isSelected))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedActivityFilterText));
            RaiseFilterChanged();
        }

        public void CollapseUnselectedProviderFilters()
        {
            foreach (var group in ProviderFilterGroups ?? Enumerable.Empty<ProviderFilterGroup>())
            {
                if (!group.HasAnySelected)
                {
                    group.IsExpanded = false;
                }
            }
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
                    L("LOCPlayAch_Filter_Games"),
                    () => SearchText = string.Empty)
            };
            controlBar.Items.Add(new GridProviderPlatformFilter(
                this,
                nameof(SelectedProviderFilterText),
                () => SelectedProviderFilterText,
                () => ProviderFilterGroups,
                CollapseUnselectedProviderFilters)
            {
                Width = 170
            });
            controlBar.Items.Add(new GridMultiSelectFilter(
                this,
                nameof(SelectedProgressFilterText),
                () => SelectedProgressFilterText,
                () => ProgressFilterOptions,
                IsProgressFilterSelected,
                SetProgressFilterSelected)
            {
                Width = 170
            });
            controlBar.Items.Add(new GridMultiSelectFilter(
                this,
                nameof(SelectedActivityFilterText),
                () => SelectedActivityFilterText,
                () => ActivityFilterOptions,
                IsActivityFilterSelected,
                SetActivityFilterSelected)
            {
                Width = 170
            });
            return controlBar;
        }

        private void OnProviderFilterSelectionChanged()
        {
            OnPropertyChanged(nameof(SelectedProviderFilterText));
            RaiseFilterChanged();
        }

        private void RaiseFilterChanged()
        {
            FilterChanged?.Invoke(this, EventArgs.Empty);
        }

        private static string GetProviderFilterDisplayName(string providerKey)
        {
            if (string.IsNullOrWhiteSpace(providerKey))
            {
                return string.Empty;
            }

            var normalized = providerKey.Trim();
            var localized = PlayniteAchievements.Providers.ProviderRegistry.GetLocalizedName(normalized);
            return string.IsNullOrWhiteSpace(localized) ? normalized : localized;
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

        private static string GetSelectedFilterText(
            HashSet<string> selectedValues,
            IEnumerable<string> options,
            string placeholder)
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

            return string.Join(", ", ordered);
        }

        private static string CompleteLabel => L("LOCPlayAch_Filter_Complete");

        private static string InProgressLabel => L("LOCPlayAch_Filter_InProgress");

        private static string NoProgressLabel => L("LOCPlayAch_Filter_NoProgress");

        private static string PlayedLabel => L("LOCPlayAch_Filter_Played");

        private static string UnplayedLabel => L("LOCPlayAch_Filter_Unplayed");

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
        }
    }
}
