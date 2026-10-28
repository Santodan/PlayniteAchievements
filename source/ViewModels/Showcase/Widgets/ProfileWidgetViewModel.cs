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
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
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
    /// Backs the Profile widget: avatar resolved from the provider identity with a manual
    /// override, the manual display name (none while blank) and background, plus a medal-count row (rarity tiers and
    /// completions, or trophy grades) and a stat strip filling the instance's configured stat
    /// slots. Medals and stats count the games its collection and control bar keep; the
    /// identity does not follow them. Density only scales the avatar; the same content shows at
    /// every size.
    /// </summary>
    public sealed class ProfileWidgetViewModel : ShowcaseWidgetViewModelBase
    {
        private readonly ShowcaseStatisticsScope _scope;
        private GridControlBarViewModel _controlBar;
        private string _backgroundPath;
        private bool _hasBackground;
        private string _avatarPath;
        private bool _hasAvatar;
        private double _avatarWidth = 72;
        private double _avatarHeight = 72;
        private double _linkIconSize = 18;
        private CornerRadius _avatarCornerRadius = new CornerRadius(12);
        private int _avatarDecodePixel = 144;
        private string _displayName;
        private bool _showDisplayName;
        private string _subtitle;
        private bool _showSubtitle;
        private bool _showMedals;
        private bool _showStatStrip;
        private int _statColumns = 4;
        private bool _isStacked;
        private bool _isCenteredRow;
        private bool _isFullBleed;
        private Thickness _contentPadding;
        private int _backgroundDecodePixel = 320;
        private bool _showLinks;
        private string _linksSignature;

        public ProfileWidgetViewModel()
        {
            _scope = new ShowcaseStatisticsScope(Refresh, capstoneJoinsTiers: true);
        }

        /// <summary>The platform, game, tag and progress filters its settings show; null when none.</summary>
        public GridControlBarViewModel ControlBar
        {
            get => _controlBar;
            private set
            {
                if (SetValueAndReturn(ref _controlBar, value))
                {
                    OnPropertyChanged(nameof(ShowControlBar));
                }
            }
        }

        public bool ShowControlBar => ControlBar != null;

        public BulkObservableCollection<BadgeCountItem> Medals { get; } =
            new BulkObservableCollection<BadgeCountItem>();

        public BulkObservableCollection<ProfileStatViewModel> Stats { get; } =
            new BulkObservableCollection<ProfileStatViewModel>();

        public BulkObservableCollection<ProfileLinkViewModel> Links { get; } =
            new BulkObservableCollection<ProfileLinkViewModel>();

        public bool ShowLinks { get => _showLinks; private set => SetValue(ref _showLinks, value); }

        public string BackgroundPath { get => _backgroundPath; private set => SetValue(ref _backgroundPath, value); }

        public bool HasBackground { get => _hasBackground; private set => SetValue(ref _hasBackground, value); }

        public string AvatarPath { get => _avatarPath; private set => SetValue(ref _avatarPath, value); }

        public bool HasAvatar { get => _hasAvatar; private set => SetValue(ref _hasAvatar, value); }

        public double AvatarWidth { get => _avatarWidth; private set => SetValue(ref _avatarWidth, value); }

        public double AvatarHeight { get => _avatarHeight; private set => SetValue(ref _avatarHeight, value); }

        public double LinkIconSize { get => _linkIconSize; private set => SetValue(ref _linkIconSize, value); }

        public CornerRadius AvatarCornerRadius { get => _avatarCornerRadius; private set => SetValue(ref _avatarCornerRadius, value); }

        public int AvatarDecodePixel { get => _avatarDecodePixel; private set => SetValue(ref _avatarDecodePixel, value); }

        public string DisplayName { get => _displayName; private set => SetValue(ref _displayName, value); }

        public bool ShowDisplayName { get => _showDisplayName; private set => SetValue(ref _showDisplayName, value); }

        public string Subtitle { get => _subtitle; private set => SetValue(ref _subtitle, value); }

        public bool ShowSubtitle { get => _showSubtitle; private set => SetValue(ref _showSubtitle, value); }

        public bool ShowMedals { get => _showMedals; private set => SetValue(ref _showMedals, value); }

        public bool ShowStatStrip { get => _showStatStrip; private set => SetValue(ref _showStatStrip, value); }

        /// <summary>Strip columns: one per stat up to four, balanced across rows past that.</summary>
        public int StatColumns { get => _statColumns; private set => SetValue(ref _statColumns, value); }

        /// <summary>Stacked layout: avatar above the text, every line centered.</summary>
        public bool IsStacked
        {
            get => _isStacked;
            private set
            {
                if (SetValueAndReturn(ref _isStacked, value))
                {
                    OnPropertyChanged(nameof(TextAlignment));
                    OnPropertyChanged(nameof(CenterStats));
                }
            }
        }

        /// <summary>Centered layout: the left layout's blocks, unchanged, centered across the card.</summary>
        public bool IsCenteredRow
        {
            get => _isCenteredRow;
            private set
            {
                if (SetValueAndReturn(ref _isCenteredRow, value))
                {
                    OnPropertyChanged(nameof(CenterStats));
                }
            }
        }

        /// <summary>Both centered layouts center a short last row of stats under the full rows.</summary>
        public bool CenterStats => IsStacked || IsCenteredRow;

        public TextAlignment TextAlignment => IsStacked ? TextAlignment.Center : TextAlignment.Left;

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
            _scope.Apply(Projection);
            ControlBar = _scope.ShownBar;
            var snapshot = _scope.Snapshot;
            var compact = Density == WidgetViewportDensity.Compact;

            BackgroundPath = resolved.BackgroundPath;
            HasBackground = !string.IsNullOrWhiteSpace(resolved.BackgroundPath);

            var layout = ShowcaseWidgetOptions.GetProfileLayout(Projection?.Instance);
            IsStacked = layout == ShowcaseProfileLayout.Stacked;
            IsCenteredRow = layout == ShowcaseProfileLayout.Centered;
            IsFullBleed = ShowcaseWidgetOptions.GetProfileFullBleed(Projection?.Instance);
            ContentPadding = IsFullBleed ? new Thickness(GetBodyInset(Density)) : new Thickness(0);
            BackgroundDecodePixel = IsFullBleed ? 640 : 320;

            AvatarPath = resolved.AvatarPath;
            HasAvatar = !string.IsNullOrWhiteSpace(resolved.AvatarPath);
            // A set width or height is fixed; an unset one follows the density.
            var densitySize = compact ? 42 : ShowcaseWidgetOptions.DefaultProfileAvatarSize;
            AvatarWidth = ShowcaseWidgetOptions.GetProfileAvatarWidthOverride(Projection?.Instance) ?? densitySize;
            AvatarHeight = ShowcaseWidgetOptions.GetProfileAvatarHeightOverride(Projection?.Instance) ?? densitySize;
            // Rounded rectangle, not a circle: the corner scales with the shorter side so every
            // size reads the same.
            AvatarCornerRadius = new CornerRadius(Math.Round(Math.Min(AvatarWidth, AvatarHeight) * 0.17));
            AvatarDecodePixel = Math.Max(64, (int)Math.Ceiling(Math.Max(AvatarWidth, AvatarHeight) * 2));
            LinkIconSize = ShowcaseWidgetOptions.GetProfileLinkIconSize(Projection?.Instance);

            DisplayName = resolved.DisplayName;
            ShowDisplayName = !string.IsNullOrWhiteSpace(resolved.DisplayName);

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
            // At most four per row, spread evenly across the rows: five stats read as 3 + 2
            // rather than 4 + 1.
            var statRows = Math.Max(1, (int)Math.Ceiling(Stats.Count / 4.0));
            StatColumns = Math.Max(1, (int)Math.Ceiling(Stats.Count / (double)statRows));
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

        private static IReadOnlyList<BadgeCountItem> BuildMedals(
            OverviewDataSnapshot snapshot,
            ShowcaseProfileMedalMode mode)
        {
            var medals = new List<BadgeCountItem>();
            if (mode == ShowcaseProfileMedalMode.None)
            {
                return medals;
            }

            if (mode != ShowcaseProfileMedalMode.Trophy)
            {
                // Completions, not completed games: a game with several capstones is finished
                // several times over, and the medal sits beside rarity counts that are all totals
                // of things earned rather than counts of games.
                BadgeCountItem.Add(medals, "BadgeCompletedGame", snapshot.Completions);
                medals.AddRange(BadgeCountItem.BuildRarity(
                    snapshot.TotalUltraRare,
                    snapshot.TotalRare,
                    snapshot.TotalUncommon,
                    snapshot.TotalCommon));
            }

            if (mode != ShowcaseProfileMedalMode.Rarity)
            {
                // Trophy grades alone skip the completions medal: they already carry the sense of
                // a finished game through the platinum. Both keeps it from the rarity row.
                BadgeCountItem.Add(medals, "TrophyPlatinum", snapshot.TotalPlatinum);
                BadgeCountItem.Add(medals, "TrophyGold", snapshot.TotalGold);
                BadgeCountItem.Add(medals, "TrophySilver", snapshot.TotalSilver);
                BadgeCountItem.Add(medals, "TrophyBronze", snapshot.TotalBronze);
            }

            return medals;
        }

        private IReadOnlyList<ProfileStatViewModel> BuildStatStrip()
        {
            var statistics = _scope.Statistics;
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
