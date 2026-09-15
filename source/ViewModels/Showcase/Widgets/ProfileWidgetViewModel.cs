using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.Services.Showcase;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>A medal-count entry: a runtime badge resource key plus its formatted count.</summary>
    public sealed class ProfileMedalViewModel
    {
        public ProfileMedalViewModel(string iconKey, string countText)
        {
            IconKey = iconKey;
            CountText = countText;
        }

        public string IconKey { get; }

        public string CountText { get; }
    }

    /// <summary>A stat-strip tile: formatted value plus localized label.</summary>
    public sealed class ProfileStatViewModel
    {
        public ProfileStatViewModel(string value, string label)
        {
            Value = value;
            Label = label;
        }

        public string Value { get; }

        public string Label { get; }
    }

    /// <summary>
    /// Backs the Profile widget: avatar, display name, and background resolved from the
    /// provider identity with manual overrides, plus a medal-count row (rarity, completed,
    /// trophies) and a stat strip filling the instance's configured stat slots. Density only
    /// scales the avatar; the same content shows at every size.
    /// </summary>
    public sealed class ProfileWidgetViewModel : ShowcaseWidgetViewModelBase
    {
        private string _backgroundPath;
        private bool _hasBackground;
        private string _avatarPath;
        private bool _hasAvatar;
        private double _avatarSize = 72;
        private CornerRadius _avatarCornerRadius = new CornerRadius(12);
        private int _avatarDecodePixel = 144;
        private string _displayName;
        private string _subtitle;
        private bool _showSubtitle;
        private bool _showMedals;
        private bool _showStatStrip;
        private int _statColumns = 4;

        public BulkObservableCollection<ProfileMedalViewModel> Medals { get; } =
            new BulkObservableCollection<ProfileMedalViewModel>();

        public BulkObservableCollection<ProfileStatViewModel> Stats { get; } =
            new BulkObservableCollection<ProfileStatViewModel>();

        public string BackgroundPath { get => _backgroundPath; private set => SetValue(ref _backgroundPath, value); }

        public bool HasBackground { get => _hasBackground; private set => SetValue(ref _hasBackground, value); }

        public string AvatarPath { get => _avatarPath; private set => SetValue(ref _avatarPath, value); }

        public bool HasAvatar { get => _hasAvatar; private set => SetValue(ref _hasAvatar, value); }

        public double AvatarSize { get => _avatarSize; private set => SetValue(ref _avatarSize, value); }

        public CornerRadius AvatarCornerRadius { get => _avatarCornerRadius; private set => SetValue(ref _avatarCornerRadius, value); }

        public int AvatarDecodePixel { get => _avatarDecodePixel; private set => SetValue(ref _avatarDecodePixel, value); }

        public string DisplayName { get => _displayName; private set => SetValue(ref _displayName, value); }

        public string Subtitle { get => _subtitle; private set => SetValue(ref _subtitle, value); }

        public bool ShowSubtitle { get => _showSubtitle; private set => SetValue(ref _showSubtitle, value); }

        public bool ShowMedals { get => _showMedals; private set => SetValue(ref _showMedals, value); }

        public bool ShowStatStrip { get => _showStatStrip; private set => SetValue(ref _showStatStrip, value); }

        /// <summary>Strip columns: one per stat up to four, wrapping to extra rows past that.</summary>
        public int StatColumns { get => _statColumns; private set => SetValue(ref _statColumns, value); }

        protected override void Refresh()
        {
            var resolved = Projection?.ResolvedProfile ?? ShowcaseProfileResolver.Resolve(
                Projection?.Profile,
                Projection?.Snapshot?.CurrentUserIdentities);
            var snapshot = Projection?.Snapshot ?? new OverviewDataSnapshot();
            var compact = Density == WidgetViewportDensity.Compact;

            BackgroundPath = resolved.BackgroundPath;
            HasBackground = !string.IsNullOrWhiteSpace(resolved.BackgroundPath);

            AvatarPath = resolved.AvatarPath;
            HasAvatar = !string.IsNullOrWhiteSpace(resolved.AvatarPath);
            AvatarSize = compact ? 42 : 72;
            // Rounded rectangle, not a circle: the corner scales with the avatar so both
            // densities read the same.
            AvatarCornerRadius = new CornerRadius(Math.Round(AvatarSize * 0.17));
            AvatarDecodePixel = Math.Max(64, (int)Math.Ceiling(AvatarSize * 2));

            DisplayName = string.IsNullOrWhiteSpace(resolved.DisplayName)
                ? ResourceProvider.GetString("LOCPlayAch_Showcase_Profile_DefaultName")
                : resolved.DisplayName;

            Subtitle = resolved.Subtitle;
            ShowSubtitle = !string.IsNullOrWhiteSpace(resolved.Subtitle);

            Medals.ReplaceAll(BuildMedals(snapshot));
            ShowMedals = Medals.Count > 0;

            Stats.ReplaceAll(BuildStatStrip());
            ShowStatStrip = Stats.Count > 0;
            StatColumns = Math.Max(1, Math.Min(4, Stats.Count));
        }

        private static IReadOnlyList<ProfileMedalViewModel> BuildMedals(OverviewDataSnapshot snapshot)
        {
            var medals = new List<ProfileMedalViewModel>();

            // Completions, not completed games: a game with several capstones is finished several
            // times over, and the medal sits beside rarity counts that are all totals of things
            // earned rather than counts of games.
            AddMedal(medals, "BadgeCompletedGame", snapshot.Completions);
            AddMedal(medals, "BadgeRarityUltraRare", snapshot.TotalUltraRare);
            AddMedal(medals, "BadgeRarityRare", snapshot.TotalRare);
            AddMedal(medals, "BadgeRarityUncommon", snapshot.TotalUncommon);
            AddMedal(medals, "BadgeRarityCommon", snapshot.TotalCommon);
            return medals;
        }

        private static void AddMedal(List<ProfileMedalViewModel> medals, string iconKey, int count)
        {
            if (count > 0)
            {
                medals.Add(new ProfileMedalViewModel(
                    iconKey,
                    count.ToString("N0", FormattingCulture.Current)));
            }
        }

        private IReadOnlyList<ProfileStatViewModel> BuildStatStrip()
        {
            var statistics = Projection?.Statistics ?? Array.Empty<ShowcaseStatistic>();
            var tiles = new List<ProfileStatViewModel>();
            foreach (var key in ShowcaseWidgetOptions.GetProfileStatKeys(Projection?.Instance))
            {
                var stat = statistics.FirstOrDefault(item =>
                    item != null && string.Equals(item.Key, key, StringComparison.Ordinal));
                if (stat == null || !stat.HasValue)
                {
                    continue;
                }

                tiles.Add(new ProfileStatViewModel(
                    ShowcaseStatisticFormatter.Format(stat),
                    ResourceProvider.GetString(stat.LabelKey)));
            }

            return tiles;
        }
    }
}
