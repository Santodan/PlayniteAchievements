using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.RetroAchievements;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    public sealed partial class ManageAchievementsViewModel
    {
        public sealed class RetroAchievementsSubsetOption : PlayniteAchievements.Common.ObservableObject
        {
            private readonly Action _selectionChanged;
            private bool _isSelected;

            public RetroAchievementsSubsetOption(int gameId, string title, bool isSelected, Action selectionChanged)
            {
                GameId = gameId;
                Title = title ?? string.Empty;
                _isSelected = isSelected;
                _selectionChanged = selectionChanged;
            }

            public int GameId { get; }

            public string Title { get; }

            public string DisplayName => $"{Title} ({GameId})";

            public bool IsSelected
            {
                get => _isSelected;
                set
                {
                    if (SetValueAndReturn(ref _isSelected, value))
                    {
                        _selectionChanged?.Invoke();
                    }
                }
            }
        }

        private int _loadedRetroAchievementsSubsetGameId;
        private int _persistedRetroAchievementsSubsetGameId;
        private List<int> _persistedRetroAchievementsSubsetIds = new List<int>();
        private readonly Dictionary<int, string> _retroAchievementsSetTitles = new Dictionary<int, string>();
        private int _resolvingRetroAchievementsSetTitleGameId;
        private int _resolvedRetroAchievementsSetTitleGameId;
        private bool _isLoadingRetroAchievementsSubsets;
        private string _retroAchievementsSubsetStatusText;

        public ObservableCollection<RetroAchievementsSubsetOption> RetroAchievementsSubsetOptions { get; } =
            new ObservableCollection<RetroAchievementsSubsetOption>();

        public AsyncCommand LoadRetroAchievementsSubsetsCommand { get; }

        public bool IsRetroAchievementsOverrideSelected =>
            string.Equals(SelectedProviderOverrideKey, "RetroAchievements", StringComparison.OrdinalIgnoreCase);

        public string RetroAchievementsGameIdInput
        {
            get => IsRetroAchievementsOverrideSelected ? ProviderOverrideInput : string.Empty;
            set
            {
                if (!IsRetroAchievementsOverrideSelected)
                {
                    SelectedProviderOverrideKey = "RetroAchievements";
                }

                ProviderOverrideInput = value ?? string.Empty;
                OnPropertyChanged();
            }
        }

        public bool HasRetroAchievementsSubsetOptions => RetroAchievementsSubsetOptions.Count > 0;

        public bool IsLoadingRetroAchievementsSubsets
        {
            get => _isLoadingRetroAchievementsSubsets;
            private set
            {
                if (SetValueAndReturn(ref _isLoadingRetroAchievementsSubsets, value))
                {
                    LoadRetroAchievementsSubsetsCommand?.RaiseCanExecuteChanged();
                }
            }
        }

        public string RetroAchievementsSubsetStatusText
        {
            get => string.IsNullOrWhiteSpace(_retroAchievementsSubsetStatusText)
                ? L("LOCPlayAch_ManageAchievements_Overrides_RaSubsets_DefaultStatus")
                : _retroAchievementsSubsetStatusText;
            private set => SetValue(ref _retroAchievementsSubsetStatusText, value);
        }

        private bool CanLoadRetroAchievementsSubsets()
        {
            return HasGame &&
                   IsRetroAchievementsOverrideSelected &&
                   !IsLoadingRetroAchievementsSubsets &&
                   int.TryParse((ProviderOverrideInput ?? string.Empty).Trim(), out var gameId) &&
                   gameId > 0;
        }

        private async Task LoadRetroAchievementsSubsetsAsync()
        {
            if (!CanLoadRetroAchievementsSubsets())
            {
                return;
            }

            var gameId = int.Parse(ProviderOverrideInput.Trim());
            var game = _playniteApi?.Database?.Games?.Get(_gameId);
            var provider = ProviderRegistry.Instance?.GetProvider("RetroAchievements") as RetroAchievementsDataProvider;
            if (game == null || provider == null)
            {
                RetroAchievementsSubsetStatusText = L("LOCPlayAch_ManageAchievements_Overrides_RaSubsets_LoadFailed");
                return;
            }

            IsLoadingRetroAchievementsSubsets = true;
            RetroAchievementsSubsetStatusText = L("LOCPlayAch_Common_Loading");
            try
            {
                var subsets = await provider
                    .GetAvailableSubsetsAsync(game, gameId, CancellationToken.None)
                    .ConfigureAwait(true);

                _loadedRetroAchievementsSubsetGameId = gameId;
                RetroAchievementsSubsetOptions.Clear();
                var savedIds = _persistedRetroAchievementsSubsetGameId == gameId
                    ? new HashSet<int>(_persistedRetroAchievementsSubsetIds)
                    : new HashSet<int>();

                foreach (var subset in subsets)
                {
                    _retroAchievementsSetTitles[subset.Id] = subset.Title ?? string.Empty;
                    RetroAchievementsSubsetOptions.Add(new RetroAchievementsSubsetOption(
                        subset.Id,
                        string.IsNullOrWhiteSpace(subset.Title) ? $"Subset {subset.Id}" : subset.Title,
                        savedIds.Contains(subset.Id),
                        UpdateRetroAchievementsSubsetStatus));
                }

                foreach (var missingId in savedIds.Where(id => RetroAchievementsSubsetOptions.All(option => option.GameId != id)))
                {
                    RetroAchievementsSubsetOptions.Add(new RetroAchievementsSubsetOption(
                        missingId,
                        $"Subset {missingId}",
                        true,
                        UpdateRetroAchievementsSubsetStatus));
                }

                OnPropertyChanged(nameof(HasRetroAchievementsSubsetOptions));
                UpdateRetroAchievementsSubsetStatus();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"[RA] Failed loading subset choices for gameId={gameId}.");
                RetroAchievementsSubsetOptions.Clear();
                OnPropertyChanged(nameof(HasRetroAchievementsSubsetOptions));
                RetroAchievementsSubsetStatusText = L("LOCPlayAch_ManageAchievements_Overrides_RaSubsets_LoadFailed");
            }
            finally
            {
                IsLoadingRetroAchievementsSubsets = false;
            }
        }

        private void ReloadRetroAchievementsSubsetState(GameCustomDataFile currentCustomData)
        {
            _loadedRetroAchievementsSubsetGameId = 0;
            RetroAchievementsSubsetOptions.Clear();
            OnPropertyChanged(nameof(HasRetroAchievementsSubsetOptions));

            _persistedRetroAchievementsSubsetIds = currentCustomData?.RetroAchievementsSelectedSubsetGameIds?
                .Where(value => value > 0)
                .Distinct()
                .ToList() ?? new List<int>();
            _persistedRetroAchievementsSubsetGameId =
                string.Equals(currentCustomData?.ProviderOverride?.ProviderKey, "RetroAchievements", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(currentCustomData.ProviderOverride.Value, out var gameId)
                    ? gameId
                    : 0;

            RetroAchievementsSubsetStatusText = _persistedRetroAchievementsSubsetIds.Count > 0
                ? string.Format(
                    L("LOCPlayAch_ManageAchievements_Overrides_RaSubsets_SavedStatus"),
                    _persistedRetroAchievementsSubsetIds.Count)
                : L("LOCPlayAch_ManageAchievements_Overrides_RaSubsets_DefaultStatus");

            if (_persistedRetroAchievementsSubsetIds.Count > 0 &&
                _persistedRetroAchievementsSubsetGameId > 0)
            {
                _ = ResolveRetroAchievementsSetTitlesAsync(_persistedRetroAchievementsSubsetGameId);
            }
        }

        private async Task ResolveRetroAchievementsSetTitlesAsync(int baseGameId)
        {
            if (baseGameId <= 0 ||
                _resolvedRetroAchievementsSetTitleGameId == baseGameId ||
                _resolvingRetroAchievementsSetTitleGameId == baseGameId)
            {
                return;
            }

            var game = _playniteApi?.Database?.Games?.Get(_gameId);
            var provider = ProviderRegistry.Instance?.GetProvider("RetroAchievements") as RetroAchievementsDataProvider;
            if (game == null || provider == null)
            {
                return;
            }

            _resolvingRetroAchievementsSetTitleGameId = baseGameId;
            try
            {
                var sets = await provider
                    .GetAvailableSubsetsAsync(game, baseGameId, CancellationToken.None)
                    .ConfigureAwait(true);
                foreach (var set in sets ?? Array.Empty<PlayniteAchievements.Providers.RetroAchievements.Models.RaSubsetEntry>())
                {
                    if (set != null && set.Id > 0)
                    {
                        _retroAchievementsSetTitles[set.Id] = set.Title ?? string.Empty;
                    }
                }

                _resolvedRetroAchievementsSetTitleGameId = baseGameId;
                RefreshOverviewOverrides();
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[RA] Failed resolving saved achievement-set titles for gameId={baseGameId}.");
            }
            finally
            {
                if (_resolvingRetroAchievementsSetTitleGameId == baseGameId)
                {
                    _resolvingRetroAchievementsSetTitleGameId = 0;
                }
            }
        }

        private string FormatRetroAchievementsSetSelection(IEnumerable<int> setIds)
        {
            return string.Join(", ", (setIds ?? Enumerable.Empty<int>()).Select(setId =>
            {
                return _retroAchievementsSetTitles.TryGetValue(setId, out var title) &&
                       !string.IsNullOrWhiteSpace(title)
                    ? $"{title.Trim()} ({setId})"
                    : setId.ToString();
            }));
        }

        private IReadOnlyCollection<int> GetRetroAchievementsSubsetIdsForSave(ProviderOverrideData providerOverride)
        {
            if (!string.Equals(providerOverride?.ProviderKey, "RetroAchievements", StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(providerOverride.Value, out var gameId))
            {
                return Array.Empty<int>();
            }

            if (_loadedRetroAchievementsSubsetGameId == gameId)
            {
                return RetroAchievementsSubsetOptions
                    .Where(option => option.IsSelected)
                    .Select(option => option.GameId)
                    .Distinct()
                    .ToList();
            }

            return _persistedRetroAchievementsSubsetGameId == gameId
                ? _persistedRetroAchievementsSubsetIds.ToList()
                : Array.Empty<int>();
        }

        private void UpdateRetroAchievementsSubsetStatus()
        {
            var selectedCount = RetroAchievementsSubsetOptions.Count(option => option.IsSelected);
            RetroAchievementsSubsetStatusText = selectedCount == 0
                ? L("LOCPlayAch_ManageAchievements_Overrides_RaSubsets_DefaultStatus")
                : string.Format(
                    L("LOCPlayAch_ManageAchievements_Overrides_RaSubsets_SelectedStatus"),
                    selectedCount);
        }

        private void OnRetroAchievementsOverrideInputChanged()
        {
            if (_loadedRetroAchievementsSubsetGameId == 0 ||
                int.TryParse((ProviderOverrideInput ?? string.Empty).Trim(), out var gameId) &&
                gameId == _loadedRetroAchievementsSubsetGameId)
            {
                return;
            }

            _loadedRetroAchievementsSubsetGameId = 0;
            RetroAchievementsSubsetOptions.Clear();
            OnPropertyChanged(nameof(HasRetroAchievementsSubsetOptions));
            RetroAchievementsSubsetStatusText =
                gameId == _persistedRetroAchievementsSubsetGameId && _persistedRetroAchievementsSubsetIds.Count > 0
                    ? L("LOCPlayAch_ManageAchievements_Overrides_RaSubsets_SavedStatus")
                    : L("LOCPlayAch_ManageAchievements_Overrides_RaSubsets_DefaultStatus");
        }
    }
}
