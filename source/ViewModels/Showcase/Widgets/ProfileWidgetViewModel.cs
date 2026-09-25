using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
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
    /// provider identity with manual overrides, plus a medal-count row (rarity tiers and
    /// completions, or trophy grades) and a stat strip filling the instance's configured stat
    /// slots. Density only scales the avatar; the same content shows at every size.
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
        private bool _isCentered;
        private bool _isFullBleed;
        private Thickness _contentPadding;
        private CornerRadius _backgroundCornerRadius;
        private int _backgroundDecodePixel = 320;

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

        /// <summary>Avatar stacked above the text, with every block centered.</summary>
        public bool IsCentered
        {
            get => _isCentered;
            private set
            {
                if (SetValueAndReturn(ref _isCentered, value))
                {
                    OnPropertyChanged(nameof(TextAlignment));
                }
            }
        }

        public TextAlignment TextAlignment => IsCentered ? TextAlignment.Center : TextAlignment.Left;

        /// <summary>
        /// The widget host drops its body inset for a full-bleed profile, so the background
        /// reaches the card edge; <see cref="ContentPadding"/> then restores the inset for the
        /// foreground alone.
        /// </summary>
        public bool IsFullBleed { get => _isFullBleed; private set => SetValue(ref _isFullBleed, value); }

        public Thickness ContentPadding { get => _contentPadding; private set => SetValue(ref _contentPadding, value); }

        /// <summary>
        /// Rounds the full-bleed background to the card's inner corners (section radius less the
        /// border), since the card clips to its bounding rectangle, not its rounded outline. The
        /// top corners stay square under a custom-title header.
        /// </summary>
        public CornerRadius BackgroundCornerRadius { get => _backgroundCornerRadius; private set => SetValue(ref _backgroundCornerRadius, value); }

        public int BackgroundDecodePixel { get => _backgroundDecodePixel; private set => SetValue(ref _backgroundDecodePixel, value); }

        protected override void Refresh()
        {
            var resolved = Projection?.ResolvedProfile ?? ShowcaseProfileResolver.Resolve(
                Projection?.Profile,
                Projection?.Snapshot?.CurrentUserIdentities);
            var snapshot = Projection?.Snapshot ?? new OverviewDataSnapshot();
            var compact = Density == WidgetViewportDensity.Compact;

            BackgroundPath = resolved.BackgroundPath;
            HasBackground = !string.IsNullOrWhiteSpace(resolved.BackgroundPath);

            IsCentered = ShowcaseWidgetOptions.GetProfileCentered(Projection?.Instance);
            IsFullBleed = ShowcaseWidgetOptions.GetProfileFullBleed(Projection?.Instance);
            ContentPadding = IsFullBleed ? new Thickness(GetBodyInset(Density)) : new Thickness(0);
            var hasHeader = !string.IsNullOrWhiteSpace(Projection?.Instance?.CustomTitle);
            var top = IsFullBleed && !hasHeader ? InnerCornerRadius : 0;
            var bottom = IsFullBleed ? InnerCornerRadius : 0;
            BackgroundCornerRadius = new CornerRadius(top, top, bottom, bottom);
            BackgroundDecodePixel = IsFullBleed ? 640 : 320;

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

            Medals.ReplaceAll(BuildMedals(
                snapshot,
                ShowcaseWidgetOptions.GetProfileMedalMode(Projection?.Instance)));
            ShowMedals = Medals.Count > 0;

            Stats.ReplaceAll(BuildStatStrip());
            ShowStatStrip = Stats.Count > 0;
            StatColumns = Math.Max(1, Math.Min(4, Stats.Count));
        }

        /// <summary><c>PlayAch.Radius.Section</c> (8) less <c>PlayAch.Thickness.Border</c> (1).</summary>
        private const double InnerCornerRadius = 7;

        private static IReadOnlyList<ProfileMedalViewModel> BuildMedals(
            OverviewDataSnapshot snapshot,
            ShowcaseProfileMedalMode mode)
        {
            var medals = new List<ProfileMedalViewModel>();
            if (mode == ShowcaseProfileMedalMode.Trophy)
            {
                // Trophy grades stand alone: they already carry the sense of a finished game
                // through the platinum, so the completions medal would double-count it.
                AddMedal(medals, "TrophyPlatinum", snapshot.TotalPlatinum);
                AddMedal(medals, "TrophyGold", snapshot.TotalGold);
                AddMedal(medals, "TrophySilver", snapshot.TotalSilver);
                AddMedal(medals, "TrophyBronze", snapshot.TotalBronze);
                return medals;
            }

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
