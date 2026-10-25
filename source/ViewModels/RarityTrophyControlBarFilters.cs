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
    /// A control bar's Rarity and Trophy filters over achievement rows. Picked rarity tiers keep
    /// a row of any of them; Capstone, in the same dropdown, keeps only capstones among those (or
    /// every capstone with no tier picked), unless <see cref="CapstoneJoinsTiers"/> makes it one
    /// more option of the set. Trophy offers nothing, which hides it, while no
    /// library game has trophies. Both dropdowns show each option's badge, and the bar shows the
    /// picked options' badges.
    /// </summary>
    public sealed class RarityTrophyControlBarFilters : PlayniteAchievements.Common.ObservableObject
    {
        /// <summary>The rarity tiers in menu order, each with its label key and badge.</summary>
        public static readonly IReadOnlyList<(RarityTier Tier, string Key, string IconKey)> RarityChoices = new[]
        {
            (RarityTier.Common, "LOCPlayAch_Rarity_Common", "BadgeRarityCommon"),
            (RarityTier.Uncommon, "LOCPlayAch_Rarity_Uncommon", "BadgeRarityUncommon"),
            (RarityTier.Rare, "LOCPlayAch_Rarity_Rare", "BadgeRarityRare"),
            (RarityTier.UltraRare, "LOCPlayAch_Rarity_UltraRare", "BadgeRarityUltraRare")
        };

        /// <summary>The trophy types in menu order, each with its label key and badge.</summary>
        public static readonly IReadOnlyList<(string Type, string Key, string IconKey)> TrophyChoices = new[]
        {
            ("platinum", "LOCPlayAch_Trophy_Platinum", "TrophyPlatinum"),
            ("gold", "LOCPlayAch_Trophy_Gold", "TrophyGold"),
            ("silver", "LOCPlayAch_Trophy_Silver", "TrophySilver"),
            ("bronze", "LOCPlayAch_Trophy_Bronze", "TrophyBronze")
        };

        // The Rarity dropdown's last option, which narrows the picked tiers to capstones.
        private const string CapstoneKey = "LOCPlayAch_Dynamic_Capstone";
        private const string CapstoneIconKey = "BadgeRarityCompleted";

        private readonly Action _changed;
        private readonly HashSet<RarityTier> _rarities = new HashSet<RarityTier>();
        private readonly HashSet<string> _trophies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _capstone;

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
                Width = 130,
                IconKeyOf = RarityIconKey
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
                Width = 130,
                IconKeyOf = TrophyIconKey
            };
        }

        public GridMultiSelectFilter RarityItem { get; }

        public GridMultiSelectFilter TrophyItem { get; }

        public ObservableCollection<string> RarityOptions { get; } = new ObservableCollection<string>(
            RarityChoices.Select(choice => ResourceProvider.GetString(choice.Key))
                .Concat(new[] { ResourceProvider.GetString(CapstoneKey) }));

        /// <summary>Empty, which hides the trophy filter, while no library game has trophies.</summary>
        public ObservableCollection<string> TrophyOptions { get; } = new ObservableCollection<string>();

        public string SelectedRarityText => SelectionText(
            RarityChoices.Where(choice => _rarities.Contains(choice.Tier)).Select(choice => choice.Key)
                .Concat(_capstone ? new[] { CapstoneKey } : Array.Empty<string>()),
            "LOCPlayAch_Column_Rarity");

        public string SelectedTrophyText => SelectionText(
            TrophyChoices.Where(choice => _trophies.Contains(choice.Type)).Select(choice => choice.Key),
            "LOCPlayAch_Column_Trophy");

        /// <summary>True while either filter would drop a row.</summary>
        public bool HasSelection => _rarities.Count > 0 || _capstone || _trophies.Count > 0;

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
            foreach (var choice in hasTrophies ? TrophyChoices : Array.Empty<(string Type, string Key, string IconKey)>())
            {
                TrophyOptions.Add(ResourceProvider.GetString(choice.Key));
            }

            return true;
        }

        /// <summary>
        /// False by default: Capstone narrows the picked tiers to capstones. True makes the Rarity
        /// options one OR set (a picked tier, or Capstone for a capstone) and gates completions on
        /// Capstone; the Profile widget sets it so its medals follow the picked options.
        /// </summary>
        public bool CapstoneJoinsTiers { get; set; }

        /// <summary>
        /// True while completions count. Always, unless <see cref="CapstoneJoinsTiers"/> and tiers
        /// are picked without Capstone.
        /// </summary>
        public bool KeepsCompletions => !CapstoneJoinsTiers || _capstone || _rarities.Count == 0;

        /// <summary>True for a row both filters keep; every row while nothing is selected.</summary>
        public bool Matches(AchievementDisplayItem row) =>
            row != null &&
            MatchesRarity(row.Rarity, row.IsCapstone) &&
            (_trophies.Count == 0 || _trophies.Contains((row.TrophyType ?? string.Empty).Trim()));

        /// <summary>
        /// True for a tally whose achievements both filters keep, locked ones included; every tally
        /// while nothing is selected. A tally of unknown tier matches no picked tier.
        /// </summary>
        public bool Matches(AchievementTally tally) =>
            MatchesRarity(tally.Rarity, tally.IsCapstone) &&
            (_trophies.Count == 0 || (tally.Trophy != null && _trophies.Contains(tally.Trophy)));

        private bool MatchesRarity(RarityTier? tier, bool isCapstone)
        {
            var tierPicked = tier.HasValue && _rarities.Contains(tier.Value);
            if (CapstoneJoinsTiers)
            {
                return (_rarities.Count == 0 && !_capstone) || tierPicked || (_capstone && isCapstone);
            }

            return (_rarities.Count == 0 || tierPicked) && (!_capstone || isCapstone);
        }

        public void Capture(ControlBarFilterState state)
        {
            state.Rarities = _rarities.Count > 0 ? _rarities.Select(tier => (int)tier).ToList() : null;
            state.Capstones = _capstone ? true : (bool?)null;
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

            _capstone = state?.Capstones == true;
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
            IsCapstoneLabel(label)
                ? _capstone
                : RarityChoices.Any(choice => _rarities.Contains(choice.Tier) &&
                                              string.Equals(ResourceProvider.GetString(choice.Key), label, StringComparison.Ordinal));

        private static bool IsCapstoneLabel(string label) =>
            string.Equals(ResourceProvider.GetString(CapstoneKey), label, StringComparison.Ordinal);

        private static string RarityIconKey(string label) =>
            IsCapstoneLabel(label)
                ? CapstoneIconKey
                : RarityChoices.FirstOrDefault(choice =>
                    string.Equals(ResourceProvider.GetString(choice.Key), label, StringComparison.Ordinal)).IconKey;

        private static string TrophyIconKey(string label) =>
            TrophyChoices.FirstOrDefault(choice =>
                string.Equals(ResourceProvider.GetString(choice.Key), label, StringComparison.Ordinal)).IconKey;

        private bool IsTrophySelected(string label) =>
            TrophyChoices.Any(choice => _trophies.Contains(choice.Type) &&
                                        string.Equals(ResourceProvider.GetString(choice.Key), label, StringComparison.Ordinal));

        private void SetRaritySelected(string label, bool selected)
        {
            if (IsCapstoneLabel(label))
            {
                if (_capstone != selected)
                {
                    _capstone = selected;
                    OnPropertyChanged(nameof(SelectedRarityText));
                    _changed?.Invoke();
                }

                return;
            }

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
