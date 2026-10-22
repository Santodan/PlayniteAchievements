using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels
{
    /// <summary>
    /// A control bar's Rarity and Trophy filters over achievement rows. Trophy offers nothing,
    /// which hides it, while no library game has trophies.
    /// </summary>
    public sealed class RarityTrophyControlBarFilters : PlayniteAchievements.Common.ObservableObject
    {
        /// <summary>The rarity tiers in menu order, each with its label key.</summary>
        public static readonly IReadOnlyList<(RarityTier Tier, string Key)> RarityChoices = new[]
        {
            (RarityTier.Common, "LOCPlayAch_Rarity_Common"),
            (RarityTier.Uncommon, "LOCPlayAch_Rarity_Uncommon"),
            (RarityTier.Rare, "LOCPlayAch_Rarity_Rare"),
            (RarityTier.UltraRare, "LOCPlayAch_Rarity_UltraRare")
        };

        /// <summary>The trophy types in menu order, each with its label key.</summary>
        public static readonly IReadOnlyList<(string Type, string Key)> TrophyChoices = new[]
        {
            ("platinum", "LOCPlayAch_Trophy_Platinum"),
            ("gold", "LOCPlayAch_Trophy_Gold"),
            ("silver", "LOCPlayAch_Trophy_Silver"),
            ("bronze", "LOCPlayAch_Trophy_Bronze")
        };

        private readonly Action _changed;
        private readonly HashSet<RarityTier> _rarities = new HashSet<RarityTier>();
        private readonly HashSet<string> _trophies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public RarityTrophyControlBarFilters(Action changed)
        {
            _changed = changed;
            RarityItem = new GridMultiSelectFilter(
                this,
                nameof(SelectedRarityText),
                () => SelectedRarityText,
                () => RarityOptions,
                IsRaritySelected,
                SetRaritySelected)
            {
                Id = ShowcaseControlBarItems.Rarity,
                LabelKey = "LOCPlayAch_Column_Rarity",
                Width = 130
            };
            TrophyItem = new GridMultiSelectFilter(
                this,
                nameof(SelectedTrophyText),
                () => SelectedTrophyText,
                () => TrophyOptions,
                IsTrophySelected,
                SetTrophySelected)
            {
                Id = ShowcaseControlBarItems.Trophy,
                LabelKey = "LOCPlayAch_Column_Trophy",
                Width = 130
            };
        }

        public GridMultiSelectFilter RarityItem { get; }

        public GridMultiSelectFilter TrophyItem { get; }

        public ObservableCollection<string> RarityOptions { get; } = new ObservableCollection<string>(
            RarityChoices.Select(choice => ResourceProvider.GetString(choice.Key)));

        /// <summary>Empty, which hides the trophy filter, while no library game has trophies.</summary>
        public ObservableCollection<string> TrophyOptions { get; } = new ObservableCollection<string>();

        public string SelectedRarityText => SelectionText(
            RarityChoices.Where(choice => _rarities.Contains(choice.Tier)).Select(choice => choice.Key),
            "LOCPlayAch_Column_Rarity");

        public string SelectedTrophyText => SelectionText(
            TrophyChoices.Where(choice => _trophies.Contains(choice.Type)).Select(choice => choice.Key),
            "LOCPlayAch_Column_Trophy");

        /// <summary>True while either filter would drop a row.</summary>
        public bool HasSelection => _rarities.Count > 0 || _trophies.Count > 0;

        /// <summary>
        /// Offers the trophy types only while some library game has trophies. True when that
        /// changed, so the bar's item visibility needs a refresh.
        /// </summary>
        public bool UpdateGames(IEnumerable<GameSummaryItem> games)
        {
            var hasTrophies = (games ?? Enumerable.Empty<GameSummaryItem>())
                .Any(game => game?.HasTrophyPieChartData == true);
            if (hasTrophies == (TrophyOptions.Count > 0))
            {
                return false;
            }

            TrophyOptions.Clear();
            foreach (var choice in hasTrophies ? TrophyChoices : Array.Empty<(string Type, string Key)>())
            {
                TrophyOptions.Add(ResourceProvider.GetString(choice.Key));
            }

            return true;
        }

        /// <summary>True for a row both filters keep; every row while nothing is selected.</summary>
        public bool Matches(AchievementDisplayItem row) =>
            row != null &&
            (_rarities.Count == 0 || _rarities.Contains(row.Rarity)) &&
            (_trophies.Count == 0 || _trophies.Contains((row.TrophyType ?? string.Empty).Trim()));

        public void Capture(ControlBarFilterState state)
        {
            state.Rarities = _rarities.Count > 0 ? _rarities.Select(tier => (int)tier).ToList() : null;
            state.Trophies = _trophies.Count > 0 ? _trophies.ToList() : null;
        }

        public void Restore(ControlBarFilterState state)
        {
            _rarities.Clear();
            foreach (var tier in state?.Rarities ?? new List<int>())
            {
                if (Enum.IsDefined(typeof(RarityTier), tier))
                {
                    _rarities.Add((RarityTier)tier);
                }
            }

            _trophies.Clear();
            foreach (var trophy in state?.Trophies ?? new List<string>())
            {
                if (TrophyChoices.Any(choice => string.Equals(choice.Type, trophy, StringComparison.OrdinalIgnoreCase)))
                {
                    _trophies.Add(trophy);
                }
            }

            OnPropertyChanged(nameof(SelectedRarityText));
            OnPropertyChanged(nameof(SelectedTrophyText));
        }

        private static string SelectionText(IEnumerable<string> selectedKeys, string placeholderKey)
        {
            var labels = selectedKeys.Select(key => ResourceProvider.GetString(key)).ToList();
            return labels.Count == 0 ? ResourceProvider.GetString(placeholderKey) : string.Join(", ", labels);
        }

        private bool IsRaritySelected(string label) =>
            RarityChoices.Any(choice => _rarities.Contains(choice.Tier) &&
                                        string.Equals(ResourceProvider.GetString(choice.Key), label, StringComparison.Ordinal));

        private bool IsTrophySelected(string label) =>
            TrophyChoices.Any(choice => _trophies.Contains(choice.Type) &&
                                        string.Equals(ResourceProvider.GetString(choice.Key), label, StringComparison.Ordinal));

        private void SetRaritySelected(string label, bool selected)
        {
            foreach (var choice in RarityChoices.Where(choice =>
                         string.Equals(ResourceProvider.GetString(choice.Key), label, StringComparison.Ordinal)))
            {
                if (selected ? _rarities.Add(choice.Tier) : _rarities.Remove(choice.Tier))
                {
                    OnPropertyChanged(nameof(SelectedRarityText));
                    _changed?.Invoke();
                }
            }
        }

        private void SetTrophySelected(string label, bool selected)
        {
            foreach (var choice in TrophyChoices.Where(choice =>
                         string.Equals(ResourceProvider.GetString(choice.Key), label, StringComparison.Ordinal)))
            {
                if (selected ? _trophies.Add(choice.Type) : _trophies.Remove(choice.Type))
                {
                    OnPropertyChanged(nameof(SelectedTrophyText));
                    _changed?.Invoke();
                }
            }
        }
    }
}
