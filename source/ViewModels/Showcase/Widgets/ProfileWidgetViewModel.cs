using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
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

    /// <summary>A clickable platform profile link, drawn as the provider's icon.</summary>
    public sealed class ProfileLinkViewModel
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        public ProfileLinkViewModel(string providerKey, string url)
        {
            Url = url;
            ProviderRegistry.TryResolveProviderVisuals(providerKey, out var iconKey, out var colorHex);
            IconKey = iconKey;
            ColorHex = colorHex;
            ToolTip = ProviderRegistry.GetLocalizedName(providerKey) + Environment.NewLine + url;
            OpenCommand = new Common.RelayCommand(_ => Open());
        }

        public string IconKey { get; }

        public string ColorHex { get; }

        public string Url { get; }

        public string ToolTip { get; }

        public Common.RelayCommand OpenCommand { get; }

        private void Open()
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = Url, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"Failed to open profile link: {Url}");
            }
        }
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
        private int _backgroundDecodePixel = 320;
        private bool _showLinks;
        private string _linksSignature;

        public BulkObservableCollection<ProfileMedalViewModel> Medals { get; } =
            new BulkObservableCollection<ProfileMedalViewModel>();

        public BulkObservableCollection<ProfileStatViewModel> Stats { get; } =
            new BulkObservableCollection<ProfileStatViewModel>();

        public BulkObservableCollection<ProfileLinkViewModel> Links { get; } =
            new BulkObservableCollection<ProfileLinkViewModel>();

        public bool ShowLinks { get => _showLinks; private set => SetValue(ref _showLinks, value); }

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

            RefreshLinks(ShowcaseWidgetOptions.GetProfileShowLinks(Projection?.Instance)
                ? resolved.Links
                : null);

            Stats.ReplaceAll(BuildStatStrip());
            ShowStatStrip = Stats.Count > 0;
            StatColumns = Math.Max(1, Math.Min(4, Stats.Count));
        }

        // Rebuilt only when the resolved links change, so an unrelated re-projection does not
        // re-render the icons.
        private void RefreshLinks(IReadOnlyList<ShowcaseProfileLinkProjection> links)
        {
            var list = (links ?? Array.Empty<ShowcaseProfileLinkProjection>())
                .Where(link => link != null && !string.IsNullOrWhiteSpace(link.Url))
                .ToList();
            var signature = string.Join("\n", list.Select(link => link.ProviderKey + "|" + link.Url));
            if (!string.Equals(signature, _linksSignature, StringComparison.Ordinal))
            {
                _linksSignature = signature;
                Links.ReplaceAll(list.Select(link => new ProfileLinkViewModel(link.ProviderKey, link.Url)).ToList());
            }

            ShowLinks = Links.Count > 0;
        }

        private static IReadOnlyList<ProfileMedalViewModel> BuildMedals(
            OverviewDataSnapshot snapshot,
            ShowcaseProfileMedalMode mode)
        {
            var medals = new List<ProfileMedalViewModel>();
            if (mode != ShowcaseProfileMedalMode.Trophy)
            {
                // Completions, not completed games: a game with several capstones is finished
                // several times over, and the medal sits beside rarity counts that are all totals
                // of things earned rather than counts of games.
                AddMedal(medals, "BadgeCompletedGame", snapshot.Completions);
                AddMedal(medals, "BadgeRarityUltraRare", snapshot.TotalUltraRare);
                AddMedal(medals, "BadgeRarityRare", snapshot.TotalRare);
                AddMedal(medals, "BadgeRarityUncommon", snapshot.TotalUncommon);
                AddMedal(medals, "BadgeRarityCommon", snapshot.TotalCommon);
            }

            if (mode != ShowcaseProfileMedalMode.Rarity)
            {
                // Trophy grades alone skip the completions medal: they already carry the sense of
                // a finished game through the platinum. Both keeps it from the rarity row.
                AddMedal(medals, "TrophyPlatinum", snapshot.TotalPlatinum);
                AddMedal(medals, "TrophyGold", snapshot.TotalGold);
                AddMedal(medals, "TrophySilver", snapshot.TotalSilver);
                AddMedal(medals, "TrophyBronze", snapshot.TotalBronze);
            }

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
