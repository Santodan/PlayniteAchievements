using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Manual;
using PlayniteAchievements.Providers.Overrides;
using PlayniteAchievements.Services.GameSearch;
using PlayniteAchievements.Views.Helpers;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    /// <summary>
    /// The platform override picked as a game: a provider whose catalog can be searched shows the
    /// overridden game's name and a Select button in place of the id box. Picking a game applies
    /// the override at once.
    /// </summary>
    public sealed partial class ManageAchievementsViewModel
    {
        private readonly Dictionary<string, IGameSearchSource> _overrideSearches =
            new Dictionary<string, IGameSearchSource>(StringComparer.OrdinalIgnoreCase);
        private string _providerOverrideGameName = string.Empty;
        private (string ProviderKey, string Value, string Name) _pickedOverride;
        private CancellationTokenSource _overrideDescribeCts;
        private RelayCommand _chooseProviderOverrideGameCommand;

        public RelayCommand ChooseProviderOverrideGameCommand =>
            _chooseProviderOverrideGameCommand ??
            (_chooseProviderOverrideGameCommand = new RelayCommand(_ => ChooseProviderOverrideGame(), _ => HasGame && IsProviderOverrideSearchVisible));

        /// <summary>True when the selected provider's games can be searched, so the override is picked rather than typed.</summary>
        public bool IsProviderOverrideSearchVisible =>
            IsProviderOverrideProviderSelected &&
            SelectedProviderOverrideValueKind == ProviderOverrideValueKind.Text &&
            GetProviderOverrideSearch(SelectedProviderOverrideKey) != null;

        /// <summary>
        /// Apply is for a value that is typed or chosen from a list, for a provider that takes no
        /// value, or for one whose empty value means "match by name" (Exophase); a picked game
        /// applies itself.
        /// </summary>
        public bool IsProviderOverrideApplyVisible =>
            !IsProviderOverrideSearchVisible ||
            GetProviderOverrideDescriptor(SelectedProviderOverrideKey)?.ValueOptional == true;

        /// <summary>The overridden game's name on the selected provider, or "Not linked" before one is picked.</summary>
        public string ProviderOverrideGameName
        {
            get => _providerOverrideGameName;
            private set => SetValue(ref _providerOverrideGameName, value ?? string.Empty);
        }

        /// <summary>The override's stored id, for the name's tooltip; null when the name is not the stored override's.</summary>
        public string ProviderOverrideIdToolTip =>
            HasProviderOverride &&
            !string.IsNullOrWhiteSpace(ProviderOverrideValue) &&
            string.Equals(NormalizeProviderOverrideSelection(SelectedProviderOverrideKey), _providerOverrideKey, StringComparison.OrdinalIgnoreCase)
                ? ProviderOverrideValue
                : null;

        private IGameSearchSource GetProviderOverrideSearch(string providerKey)
        {
            var key = NormalizeProviderOverrideSelection(providerKey);
            if (!_overrideSearches.TryGetValue(key, out var search))
            {
                search = CreateProviderOverrideSearch(key);
                _overrideSearches[key] = search;
            }

            return search;
        }

        private IGameSearchSource CreateProviderOverrideSearch(string providerKey)
        {
            var option = FindProviderOverrideOption(providerKey);
            var game = _gameId == Guid.Empty ? null : _playniteApi?.Database?.Games?.Get(_gameId);
            if (option?.Provider == null || game == null)
            {
                return null;
            }

            try
            {
                if (option.Provider is IProviderOverrideSearch searchable)
                {
                    return searchable.CreateOverrideSearch(game);
                }

                // A provider that stores the same ids as a manual tracking source (Steam appIds,
                // Exophase slugs) is searched through that source's catalog.
                var manual = _plugin?.ManualSources?.GetSourceByKey(option.ProviderKey);
                return manual == null
                    ? null
                    : new ManualSourceGameSearch(manual, _settings?.Persisted?.GlobalLanguage, displayName: option.DisplayName);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[ManageAchievements] No override search for {option.ProviderKey}.");
                return null;
            }
        }

        private void ChooseProviderOverrideGame()
        {
            var providerKey = NormalizeProviderOverrideSelection(SelectedProviderOverrideKey);
            var search = GetProviderOverrideSearch(providerKey);
            if (search == null)
            {
                return;
            }

            var current = HasProviderOverride &&
                          string.Equals(_providerOverrideKey, providerKey, StringComparison.OrdinalIgnoreCase) &&
                          !string.IsNullOrWhiteSpace(ProviderOverrideValue)
                ? new[] { new GameSearchResult(ProviderOverrideValue, ProviderOverrideGameName) }
                : null;
            var title = string.Join(
                " · ",
                new[] { FindProviderOverrideOption(providerKey)?.DisplayName, GameName }
                    .Where(part => !string.IsNullOrWhiteSpace(part)));
            var picked = GameSearchWindow.Show(null, search, title, GameName, current)?.FirstOrDefault();
            if (picked == null)
            {
                return;
            }

            if (!TryCreateProviderOverride(providerKey, picked.Id, out var providerOverride, out var validationMessage))
            {
                _playniteApi?.Dialogs?.ShowMessage(
                    validationMessage,
                    L("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _pickedOverride = (
                providerOverride.ProviderKey,
                providerOverride.Value,
                string.Equals(picked.Name, picked.Id, StringComparison.OrdinalIgnoreCase) ? null : picked.Name);
            if (TrySetProviderOverride(providerOverride))
            {
                Reload();
            }
        }

        /// <summary>
        /// Names the overridden game: the one picked this session, else the cached game's name
        /// when the cache comes from the override's provider, else the provider's own answer for
        /// the id, shown once it arrives (the id until then).
        /// </summary>
        private void ReloadProviderOverrideGameName()
        {
            _overrideDescribeCts?.Cancel();
            _overrideDescribeCts = null;
            OnPropertyChanged(nameof(ProviderOverrideIdToolTip));

            var providerKey = _providerOverrideKey;
            var value = ProviderOverrideValue;
            if (!HasProviderOverride ||
                !string.Equals(NormalizeProviderOverrideSelection(SelectedProviderOverrideKey), providerKey, StringComparison.OrdinalIgnoreCase))
            {
                ProviderOverrideGameName = L("LOCPlayAch_Common_Status_NotLinked");
                return;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                ProviderOverrideGameName = string.Empty;
                return;
            }

            if (string.Equals(_pickedOverride.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_pickedOverride.Value, value, StringComparison.OrdinalIgnoreCase))
            {
                ProviderOverrideGameName = _pickedOverride.Name ?? value;
                return;
            }

            var cached = _plugin?.AchievementDataService?.GetGameAchievementData(_gameId);
            if (cached != null &&
                string.Equals(cached.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(cached.GameName))
            {
                ProviderOverrideGameName = cached.GameName;
                return;
            }

            ProviderOverrideGameName = value;
            var search = GetProviderOverrideSearch(providerKey);
            if (search != null)
            {
                var cts = new CancellationTokenSource();
                _overrideDescribeCts = cts;
                _ = DescribeProviderOverrideAsync(search, providerKey, value, cts.Token);
            }
        }

        private async Task DescribeProviderOverrideAsync(IGameSearchSource search, string providerKey, string value, CancellationToken ct)
        {
            try
            {
                var described = await search.DescribeAsync(value, ct);
                if (!ct.IsCancellationRequested &&
                    !string.IsNullOrWhiteSpace(described?.Name) &&
                    string.Equals(_providerOverrideKey, providerKey, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(ProviderOverrideValue, value, StringComparison.OrdinalIgnoreCase))
                {
                    ProviderOverrideGameName = described.Name;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[ManageAchievements] Naming the {providerKey} override {value} failed.");
            }
        }
    }
}
