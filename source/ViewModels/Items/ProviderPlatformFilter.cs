using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using PlayniteAchievements.Common;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.ViewModels.Items
{
    /// <summary>
    /// A single selectable platform (console) under a provider in the Overview platform filter.
    /// </summary>
    public sealed class PlatformFilterOption : ObservableObject
    {
        public PlatformFilterOption(string platformName, bool isSelected = false)
        {
            PlatformName = platformName;
            _isSelected = isSelected;
        }

        public string PlatformName { get; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set => SetValue(ref _isSelected, value);
        }
    }

    /// <summary>
    /// A provider row in the Overview platform filter. Owns the per-platform options and derives a
    /// tri-state selection summary for the parent checkbox. A provider with a single platform shows
    /// no expander and behaves as a plain checkbox; the parent toggle selects/clears all platforms.
    /// </summary>
    public sealed class ProviderFilterGroup : ObservableObject
    {
        private readonly Action _onSelectionChanged;
        private bool _isBulkUpdating;

        public ProviderFilterGroup(
            string providerKey,
            string displayName,
            IEnumerable<string> platformNames,
            Func<string, bool> isPlatformSelected,
            Action onSelectionChanged)
        {
            ProviderKey = providerKey;
            DisplayName = displayName;
            _onSelectionChanged = onSelectionChanged;

            var options = new List<PlatformFilterOption>();
            foreach (var name in platformNames ?? Enumerable.Empty<string>())
            {
                var option = new PlatformFilterOption(name, isPlatformSelected?.Invoke(name) ?? false);
                option.PropertyChanged += OnOptionPropertyChanged;
                options.Add(option);
            }

            Platforms = new ObservableCollection<PlatformFilterOption>(options);
            ToggleAllCommand = new RelayCommand(_ => ToggleAll());
        }

        public string ProviderKey { get; }

        public string DisplayName { get; }

        public ObservableCollection<PlatformFilterOption> Platforms { get; }

        /// <summary>
        /// True when more than one platform exists, so the row shows an inline expander.
        /// </summary>
        public bool HasMultiplePlatforms => Platforms.Count > 1;

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetValue(ref _isExpanded, value);
        }

        public RelayCommand ToggleAllCommand { get; }

        /// <summary>
        /// Tri-state summary for the parent checkbox: true = all selected, false = none, null = some.
        /// </summary>
        public bool? SelectionState
        {
            get
            {
                var selected = Platforms.Count(p => p.IsSelected);
                if (selected == 0)
                {
                    return false;
                }

                if (selected == Platforms.Count)
                {
                    return true;
                }

                return null;
            }
        }

        /// <summary>
        /// True when at least one platform is selected (the provider is "active" for filtering).
        /// </summary>
        public bool HasAnySelected => Platforms.Any(p => p.IsSelected);

        /// <summary>
        /// True when every platform is selected (the provider is fully selected).
        /// </summary>
        public bool IsFullySelected => Platforms.Count > 0 && Platforms.All(p => p.IsSelected);

        /// <summary>
        /// Selected platform names in display order.
        /// </summary>
        public IEnumerable<string> SelectedPlatformNames =>
            Platforms.Where(p => p.IsSelected).Select(p => p.PlatformName);

        /// <summary>
        /// Selects all platforms when not all are currently selected; otherwise clears them.
        /// </summary>
        public void ToggleAll()
        {
            SetAll(SelectionState != true);
        }

        /// <summary>
        /// Sets every platform's selection in one batch, firing a single change notification.
        /// </summary>
        public void SetAll(bool isSelected)
        {
            _isBulkUpdating = true;
            try
            {
                foreach (var option in Platforms)
                {
                    option.IsSelected = isSelected;
                }
            }
            finally
            {
                _isBulkUpdating = false;
            }

            RaiseSelectionChanged();
        }

        private void OnOptionPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(PlatformFilterOption.IsSelected) || _isBulkUpdating)
            {
                return;
            }

            RaiseSelectionChanged();
        }

        private void RaiseSelectionChanged()
        {
            OnPropertyChanged(nameof(SelectionState));
            OnPropertyChanged(nameof(HasAnySelected));
            OnPropertyChanged(nameof(IsFullySelected));
            _onSelectionChanged?.Invoke();
        }
    }

    /// <summary>
    /// Builds provider filter groups from a set of game summaries: games grouped by provider key,
    /// distinct platform names sorted per provider, a synthetic provider-named option when a
    /// provider has no platform metadata, and prior selections/expansion carried over from the
    /// previous groups.
    /// </summary>
    public static class ProviderFilterGroupBuilder
    {
        public static ObservableCollection<ProviderFilterGroup> Rebuild(
            IEnumerable<GameSummaryItem> games,
            IEnumerable<ProviderFilterGroup> existingGroups,
            Action onSelectionChanged)
        {
            var priorSelections = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var priorExpanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var existing in existingGroups ?? Enumerable.Empty<ProviderFilterGroup>())
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
            var displayNamesByProvider = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var game in games ?? Enumerable.Empty<GameSummaryItem>())
            {
                var providerKey = game?.ProviderFilterKey;
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

                // Prefer the item's already-resolved display name so the filter labels match the
                // grid's provider column (covers display keys with no registry entry).
                if (!displayNamesByProvider.ContainsKey(providerKey) &&
                    !string.IsNullOrWhiteSpace(game.Provider))
                {
                    displayNamesByProvider[providerKey] = game.Provider.Trim();
                }

                foreach (var platform in game.Platforms ?? Array.Empty<string>())
                {
                    if (!string.IsNullOrWhiteSpace(platform))
                    {
                        platforms.Add(platform.Trim());
                    }
                }
            }

            string GetDisplayName(string providerKey) =>
                displayNamesByProvider.TryGetValue(providerKey, out var name)
                    ? name
                    : GetProviderFilterDisplayName(providerKey);

            var groups = new List<ProviderFilterGroup>();
            foreach (var providerKey in platformsByProvider.Keys
                .OrderBy(GetDisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                var platformNames = platformsByProvider[providerKey].ToList();
                if (platformNames.Count == 0)
                {
                    platformNames.Add(GetDisplayName(providerKey));
                }

                priorSelections.TryGetValue(providerKey, out var selectedSet);
                groups.Add(new ProviderFilterGroup(
                    providerKey,
                    GetDisplayName(providerKey),
                    platformNames,
                    name => selectedSet != null && selectedSet.Contains(name),
                    onSelectionChanged)
                {
                    IsExpanded = priorExpanded.Contains(providerKey)
                });
            }

            return new ObservableCollection<ProviderFilterGroup>(groups);
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
    }
}
