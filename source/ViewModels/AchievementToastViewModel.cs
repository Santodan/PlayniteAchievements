using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.ViewModels
{
    public sealed class AchievementToastViewModel
    {
        private const string DefaultIcon =
            "pack://application:,,,/PlayniteAchievements;component/Resources/UnlockedAchIcon.png";

        // Frame font fallbacks are 1080-reference-canvas DIPs matching the bundled frame
        // template's historical literals, deliberately independent of theme font sizes.
        private const double FrameHeaderFontFallback = 17;
        private const double FrameTitleFontFallback = 30;
        private const double FrameBodyFontFallback = 19;
        private const double FrameGameCategoryFontFallback = 19;

        private readonly AchievementUnlockedEventArgs _args;
        private readonly PersistedSettings _settings;
        private readonly NotificationStyleSettings _style;
        private readonly RarityTier _rarity;
        private IReadOnlyList<ToastLineDescriptor> _toastLines;
        private IReadOnlyList<ToastLineDescriptor> _frameLines;

        public AchievementToastViewModel(
            AchievementUnlockedEventArgs args,
            PersistedSettings settings,
            NotificationStyleSettings styleOverride = null)
        {
            _args = args ?? new AchievementUnlockedEventArgs();
            _settings = settings ?? new PersistedSettings();
            _style = styleOverride ?? NotificationStyleResolver.Resolve(_settings, _args.ProviderKey);
            _rarity = ParseRarity(_args.RarityTier);
        }

        public bool IsFriendUnlock => _args.IsFriendUnlock;

        // Provider identity, bindable so a single toast/frame template can restyle per provider
        // with DataTriggers (e.g. trigger on ProviderKey, tint with ProviderColorHex).
        public string ProviderKey => _args.ProviderKey;
        public string ProviderName => Providers.ProviderRegistry.GetLocalizedName(ProviderKey);
        public string ProviderColorHex => Providers.ProviderRegistry.GetProviderColorHex(ProviderKey);

        // Raw fields consumed by the unlock-screenshot feature (not shown in the toast UI).
        internal bool IsPreview => _args.IsPreview;
        internal string AchievementName => ResolveAchievementName(_args);
        internal int AchievementNumber => _args.AchievementNumber;

        /// <summary>
        /// The unlock's name for screenshot/clip filenames and clip-to-wave matching: the
        /// achievement's display name, or the localized "Game Complete!" for the completion
        /// notification (which carries no display name). Shared with the recording service so
        /// both sides agree on the name.
        /// </summary>
        internal static string ResolveAchievementName(AchievementUnlockedEventArgs args)
        {
            return args?.IsGameCompleted == true
                ? ResourceProvider.GetString("LOCPlayAch_Toast_GameComplete")
                : args?.DisplayName;
        }
        internal Guid PlayniteGameId => _args.PlayniteGameId;

        // Raw progress and scoring data for template composition (e.g. a "27/40" progress line
        // or a points tag). Points are provider-specific and null when the provider has none.
        public int UnlockedCount => _args.UnlockedCount;
        public int TotalCount => _args.TotalCount;
        public int? Points => _args.Points;
        public int? ScaledPoints => _args.ScaledPoints;

        // The header identifies who unlocked the achievement, so it is mandatory for friend
        // unlocks; for your own unlocks it honors the user's toggle. Completion notifications are
        // restyled entirely by the templates (triggers on IsGameCompleted force the header/title/
        // game name visible there), never here.
        public bool ShowHeader => IsFriendUnlock || _style.Toast.ShowHeader;
        public bool ShowName => _style.Toast.ShowName && !string.IsNullOrWhiteSpace(TitleText);
        public bool ShowDescription => _style.Toast.ShowDescription && !string.IsNullOrWhiteSpace(_args.Description);
        public bool ShowCategory => _style.Toast.ShowCategory && HasDistinctCategory;
        public bool ShowPercent => _style.Toast.ShowRarityPercent && _args.GlobalPercent.HasValue;
        public bool IsCapstone => _args.IsCapstone;

        /// <summary>
        /// True for the standalone "Congratulations! Game Complete!" notification that follows
        /// the completing unlock's wave. Regular unlocks — including the completion achievement
        /// itself — report false.
        /// </summary>
        public bool IsGameCompleted => _args.IsGameCompleted;

        /// <summary>
        /// True on a real achievement unlock when the game is complete after it (all
        /// achievements unlocked, or the capstone unlocked) — computed for your own unlocks and
        /// friend unlocks alike, so a template can restyle the unlock that finished the game.
        /// The standalone IsGameCompleted notification reports false here.
        /// </summary>
        public bool IsCompletionAchievement => _args.IsCompletionAchievement;

        public bool HasTrophy => !string.IsNullOrWhiteSpace(_args.TrophyType);

        /// <summary>
        /// Canonical trophy tier for trophy-based providers: "Platinum", "Gold", "Silver", or
        /// "Bronze" (normalized casing so DataTrigger Value= matching works regardless of what
        /// the provider reported); empty when the unlock has no trophy. Mirrors the tier
        /// fallback of MapTrophyKey/BadgeImage.
        /// </summary>
        public string TrophyType
        {
            get
            {
                if (!HasTrophy)
                {
                    return string.Empty;
                }

                switch (_args.TrophyType.Trim().ToLowerInvariant())
                {
                    case "platinum":
                        return "Platinum";
                    case "gold":
                        return "Gold";
                    case "silver":
                        return "Silver";
                    default:
                        return "Bronze";
                }
            }
        }
        private bool HasRarityData => _args.GlobalPercent.HasValue || !string.IsNullOrWhiteSpace(_args.RarityTier);
        public bool ShowBadge => _style.Toast.ShowRarityBadge && (IsCapstone || HasTrophy || HasRarityData);
        public bool ShowGameName => _style.Toast.ShowGameName && !string.IsNullOrWhiteSpace(_args.GameName);
        public bool ShowGameCategorySeparator => ShowGameName && ShowCategory;
        public bool HasFriendAvatar => !string.IsNullOrWhiteSpace(FriendAvatar);

        // A category that just repeats the game name (common for single-list providers) reads as
        // a duplicate, so it is force-hidden in both the toast and the frame.
        private bool HasDistinctCategory =>
            !string.IsNullOrWhiteSpace(_args.Category) &&
            !string.Equals(_args.Category?.Trim(), _args.GameName?.Trim(), StringComparison.OrdinalIgnoreCase);

        // Unlock timestamp bindings (local time, current-culture formatting like the grids'
        // UnlockTimeText). A midnight time-of-day means a date-only provider timestamp, so the
        // time portion is suppressed. Available to both toast and frame templates.
        private DateTime? UnlockTimeLocal => Common.DateTimeUtilities.AsLocalFromUtc(_args.UnlockTimeUtc);
        public bool HasUnlockTime => UnlockTimeLocal.HasValue;
        // Toast-scoped visibility for the unlock datetime on the header line (off by default).
        // The header toggle governs only the "Achievement unlocked" text; the datetime is
        // independent, and the separator needs both.
        public bool ShowUnlockTime => _style.Toast.ShowUnlockTime && HasUnlockTime;
        public bool ShowHeaderDateSeparator => ShowHeader && ShowUnlockTime;
        public bool ShowFriendAvatar => ShowHeader && HasFriendAvatar;
        public string UnlockDateText => UnlockTimeLocal?.ToString("d") ?? string.Empty;
        public string UnlockTimeText => UnlockTimeLocal.HasValue && UnlockTimeLocal.Value.TimeOfDay != TimeSpan.Zero
            ? UnlockTimeLocal.Value.ToString("t")
            : string.Empty;
        public string UnlockDateTimeText => UnlockTimeLocal.HasValue
            ? UnlockTimeLocal.Value.TimeOfDay != TimeSpan.Zero
                ? UnlockTimeLocal.Value.ToString("g")
                : UnlockTimeLocal.Value.ToString("d")
            : string.Empty;

        // Frame-scoped equivalents: the frame's header row shows "header • unlock datetime";
        // the separator needs both, and the datetime honors its own toggle.
        public bool FrameShowUnlockTime => _style.Frame.ShowUnlockTime && HasUnlockTime;
        public bool FrameShowHeaderDateSeparator => FrameShowHeader && FrameShowUnlockTime;

        // Frame-scoped visibility/appearance: the screenshot frame honors its own FrameShow*
        // settings so the saved image can show different fields than the on-screen toast.
        public bool FrameShowHeader => IsFriendUnlock || _style.Frame.ShowHeader;
        public bool FrameShowName => _style.Frame.ShowName && !string.IsNullOrWhiteSpace(TitleText);
        public bool FrameShowDescription => _style.Frame.ShowDescription && !string.IsNullOrWhiteSpace(_args.Description);
        public bool FrameShowCategory => _style.Frame.ShowCategory && HasDistinctCategory;
        public bool FrameShowPercent => _style.Frame.ShowRarityPercent && _args.GlobalPercent.HasValue;
        public bool FrameShowBadge => _style.Frame.ShowRarityBadge && (IsCapstone || HasTrophy || HasRarityData);
        public bool FrameShowGameName => _style.Frame.ShowGameName && !string.IsNullOrWhiteSpace(_args.GameName);
        public bool FrameShowGameCategorySeparator => FrameShowGameName && FrameShowCategory;
        public bool FrameShowShineBorder => _style.Frame.ShowRarityGlow && IsHardcore;

        // Mirrors TitleBrush but honors the frame's own rarity-colored-name toggle.
        public Brush FrameTitleBrush => _style.Frame.RarityColoredName
            ? AccentBrush
            : Application.Current?.TryFindResource("PlayAch.Brush.Text") as Brush ?? Brushes.White;

        public Effect FrameRarityGlowEffect => _style.Frame.ShowRarityGlow && !IsHardcore
            ? RarityAppearanceHelper.GetGlow(_rarity, 20, _settings)
            : null;

        // Header texts honor the style's user edits with the localized strings as fallback.
        // Stored friend formats that lost their {0} placeholder fall back to the localized
        // default rather than crashing the toast.
        public string HeaderText
        {
            get
            {
                if (IsFriendUnlock)
                {
                    var format = NotificationHeaderTextService.IsValidHeaderFormat(_style.HeaderTexts.FriendUnlockHeaderFormat)
                        ? _style.HeaderTexts.FriendUnlockHeaderFormat
                        : ResourceProvider.GetString("LOCPlayAch_Toast_FriendUnlocked");
                    return string.Format(format, FriendDisplayName);
                }

                return !string.IsNullOrWhiteSpace(_style.HeaderTexts.UnlockHeader)
                    ? _style.HeaderTexts.UnlockHeader
                    : ResourceProvider.GetString("LOCPlayAch_Toast_AchievementUnlocked");
            }
        }

        /// <summary>
        /// Header text of the standalone game-completion notification ("Congratulations!" by
        /// default), honoring the style's user edit.
        /// </summary>
        public string CompletionHeaderText => !string.IsNullOrWhiteSpace(_style.HeaderTexts.CompletionHeader)
            ? _style.HeaderTexts.CompletionHeader
            : ResourceProvider.GetString("LOCPlayAch_Toast_Congratulations");

        /// <summary>
        /// Header text of a friend's game-completion notification ("{friend} completed the
        /// game!" by default), honoring the style's user edit.
        /// </summary>
        public string FriendCompletionHeaderText
        {
            get
            {
                var format = NotificationHeaderTextService.IsValidHeaderFormat(_style.HeaderTexts.FriendCompletionHeaderFormat)
                    ? _style.HeaderTexts.FriendCompletionHeaderFormat
                    : "{0} " + ResourceProvider.GetString("LOCPlayAch_Toast_CompletedTheGame");
                return string.Format(format, FriendDisplayName);
            }
        }

        public string TitleText => string.IsNullOrWhiteSpace(_args.DisplayName)
            ? ResourceProvider.GetString("LOCPlayAch_Text_UnknownAchievement")
            : _args.DisplayName;

        // Raw friend identity for template composition (e.g. the friend completion header). The
        // completion texts themselves live in the templates as LOC resources, not here.
        public string FriendDisplayName => string.IsNullOrWhiteSpace(_args.FriendDisplayName)
            ? "Friend"
            : _args.FriendDisplayName;

        public string Description => _args.Description;
        public string Category => _args.Category;
        public string GameName => _args.GameName;

        // Absolute local paths to the Playnite game's icon and cover art; null when the game has
        // none (e.g. previews). Local files, so frame templates may bind Image.Source directly.
        public string GameIconPath => _args.GameIconPath;
        public string GameCoverPath => _args.GameCoverPath;
        public string IconPath => string.IsNullOrWhiteSpace(_args.IconPath) ? DefaultIcon : _args.IconPath;
        public string FriendAvatar => !string.IsNullOrWhiteSpace(_args.FriendAvatarPath)
            ? _args.FriendAvatarPath
            : _args.FriendAvatarUrl;

        public string PercentText => _args.GlobalPercent.HasValue
            ? AchievementRarityResolver.FormatPercent(_args.GlobalPercent.Value)
            : string.Empty;

        /// <summary>
        /// Parsed rarity tier for theme authors. Templates can bind to this directly for custom
        /// badge styles/triggers instead of using the plugin-generated BadgeImage.
        /// </summary>
        public RarityTier Rarity => _rarity;

        /// <summary>
        /// Rarity-colored brush for the left accent strip and countdown bar (completed color for
        /// capstones, otherwise the rarity color). Completion notifications keep this untouched —
        /// the templates restyle them with the palette below.
        /// </summary>
        public Brush AccentBrush => IsCapstone
            ? RarityAppearanceHelper.GetCompletedBrush(_settings)
            : RarityAppearanceHelper.GetBrush(_rarity, _settings);

        // Completion palette, always available regardless of this notification's kind so the
        // bundled templates (and themes) apply completion styling with triggers on
        // IsGameCompleted / IsCompletionAchievement. The glows honor the rarity-glow toggles.
        public Brush CompletedBrush => RarityAppearanceHelper.GetCompletedBrush(_settings);
        public Effect CompletedGlowEffect => _style.Toast.ShowRarityGlow
            ? RarityAppearanceHelper.GetCompletedGlow(useEndColor: true, _settings)
            : null;
        public Effect FrameCompletedGlowEffect => _style.Frame.ShowRarityGlow
            ? RarityAppearanceHelper.GetCompletedGlow(useEndColor: true, _settings)
            : null;
        public ImageSource CompletedBadgeImage => RarityAppearanceHelper.CreateCompletedBadgePreview(_settings);
        public Brush RarityBrush => RarityAppearanceHelper.GetBrush(_rarity, _settings);

        // Capstone color takes precedence over rarity, matching the grid's RarityNameBrush.
        public Brush TitleBrush => _style.Toast.RarityColoredName
            ? AccentBrush
            : Application.Current?.TryFindResource("PlayAch.Brush.Text") as Brush ?? Brushes.White;

        public bool IsHardcore => _args.IsHardcore;

        /// <summary>
        /// Hardcore RetroAchievements unlocks get a crisp rarity-colored border in place of the
        /// soft glow, mirroring the datagrids. Both are gated on the rarity-glow toggle.
        /// </summary>
        public bool ShowShineBorder => _style.Toast.ShowRarityGlow && IsHardcore;

        // Glossy metallic rarity border (matches RarityToShineBrush used by the datagrids).
        public Brush IconBorderBrush => RarityAppearanceHelper.GetShineBrush(_rarity, _settings);

        // Soft rarity glow for non-hardcore unlocks (matches PercentToRarityGlow, BlurRadius 20).
        public Effect RarityGlowEffect => _style.Toast.ShowRarityGlow && !IsHardcore
            ? RarityAppearanceHelper.GetGlow(_rarity, 20, _settings)
            : null;

        // Secondary rarity/trophy/capstone badge. Completion notifications resolve to null
        // naturally (no capstone, trophy, or rarity data on them).
        public ImageSource BadgeImage => CreateBadge(IsCapstone);

        private ImageSource CreateBadge(bool completed)
        {
            if (completed)
            {
                return RarityAppearanceHelper.CreateCompletedBadgePreview(_settings);
            }

            if (HasTrophy)
            {
                return RarityAppearanceHelper.CreateTrophyPreview(MapTrophyKey(_args.TrophyType), _settings);
            }

            return HasRarityData
                ? RarityAppearanceHelper.CreateBadgePreview(_rarity, _settings)
                : null;
        }

        /// <summary>
        /// User badge image for this unlock's badge slot, or null for the drawn badge.
        /// Capstones use the completion slot; otherwise a set rarity image wins over the
        /// trophy badge (trophy-typed unlocks carry rarity data too), which is the documented
        /// custom-rarity-beats-trophy rule.
        /// </summary>
        private string CustomBadgeImagePath
        {
            get
            {
                var badges = _style.BadgeImages;
                if (IsCapstone)
                {
                    return NullIfBlank(badges.CompletionPath);
                }

                if (!HasRarityData)
                {
                    return null;
                }

                switch (_rarity)
                {
                    case RarityTier.UltraRare:
                        return NullIfBlank(badges.UltraRarePath);
                    case RarityTier.Rare:
                        return NullIfBlank(badges.RarePath);
                    case RarityTier.Uncommon:
                        return NullIfBlank(badges.UncommonPath);
                    default:
                        return NullIfBlank(badges.CommonPath);
                }
            }
        }

        // Badge source for the toast template's AsyncImage binding: the custom image path
        // when one is set (a string, so animated GIF badges animate), otherwise the drawn
        // badge ImageSource.
        public object ToastBadgeSource => (object)CustomBadgeImagePath ?? BadgeImage;

        // Toast icon-swap source for the completion trigger, mirroring CompletedBadgeImage.
        public object ToastCompletedBadgeSource =>
            (object)NullIfBlank(_style.BadgeImages.CompletionPath) ?? CompletedBadgeImage;

        // Frame equivalents: the frame is rendered offscreen, so images must be synchronously
        // decoded (an async load renders blank); animated GIFs contribute their first frame.
        public ImageSource FrameBadgeImage => LoadSyncImage(CustomBadgeImagePath) ?? BadgeImage;

        public ImageSource FrameCompletedBadgeImage =>
            LoadSyncImage(NullIfBlank(_style.BadgeImages.CompletionPath)) ?? CompletedBadgeImage;

        /// <summary>
        /// Provider icon geometry key for the toast/frame provider icon (rendered via
        /// ProviderIconConverter with <see cref="ProviderColorHex"/>); null when the provider
        /// is unknown (e.g. tests or previews without a registry).
        /// </summary>
        public string ProviderIconKey
        {
            get
            {
                Providers.ProviderRegistry.TryResolveProviderVisuals(ProviderKey, out var iconKey, out _);
                return iconKey;
            }
        }

        public bool ShowProviderIcon => _style.Toast.ShowProviderIcon && !string.IsNullOrWhiteSpace(ProviderIconKey);
        public bool FrameShowProviderIcon => _style.Frame.ShowProviderIcon && !string.IsNullOrWhiteSpace(ProviderIconKey);

        // User toast background image (frames never get a background). Missing files fall
        // back to the default surface brush via HasToastBackground.
        public string ToastBackgroundImagePath => _style.ToastBackgroundImagePath;
        public bool HasToastBackground
        {
            get
            {
                var path = _style.ToastBackgroundImagePath;
                return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
            }
        }

        // Effective font family per surface: the style's family when set, otherwise the
        // theme-derived body family.
        public FontFamily ToastFontFamily => ResolveFontFamily(_style.Toast.FontFamily);
        public FontFamily FrameFontFamily => ResolveFontFamily(_style.Frame.FontFamily);

        // Effective caption/header size per surface, also used by the icon column's percent
        // text (part of the "header/caption" size group).
        public double ToastHeaderFontSize => _style.Toast.HeaderFontSize
            ?? ResolveFontSizeResource("PlayAch.FontSize.Caption", 11);
        public double FrameHeaderFontSize => _style.Frame.HeaderFontSize ?? FrameHeaderFontFallback;

        /// <summary>
        /// The toast's text lines in the user's order; hidden lines are still present with
        /// their visibility flags false so completion triggers can force them visible.
        /// </summary>
        public IReadOnlyList<ToastLineDescriptor> ToastLines =>
            _toastLines ?? (_toastLines = BuildLines(isFrame: false));

        /// <summary>
        /// The frame's text lines in the user's order. Frame lines never show the friend
        /// avatar (async image loads render blank in the offscreen composite).
        /// </summary>
        public IReadOnlyList<ToastLineDescriptor> FrameLines =>
            _frameLines ?? (_frameLines = BuildLines(isFrame: true));

        private IReadOnlyList<ToastLineDescriptor> BuildLines(bool isFrame)
        {
            var surface = isFrame ? _style.Frame : _style.Toast;
            var family = isFrame ? FrameFontFamily : ToastFontFamily;
            var headerSize = isFrame ? FrameHeaderFontSize : ToastHeaderFontSize;
            var titleSize = surface.TitleFontSize ??
                (isFrame ? FrameTitleFontFallback : ResolveFontSizeResource("PlayAch.FontSize.Title", 16));
            var bodySize = surface.BodyFontSize ??
                (isFrame ? FrameBodyFontFallback : ResolveFontSizeResource("PlayAch.FontSize.Caption", 11));
            var gameCategorySize = surface.HeaderFontSize ??
                (isFrame ? FrameGameCategoryFontFallback : ResolveFontSizeResource("PlayAch.FontSize.Caption", 11));

            var lines = new List<ToastLineDescriptor>(NotificationSurfaceStyle.DefaultLineOrder.Count);
            foreach (var token in NotificationSurfaceStyle.CanonicalizeLineOrder(surface.LineOrder))
            {
                switch (token)
                {
                    case NotificationSurfaceStyle.LineHeader:
                        lines.Add(new ToastHeaderLine(
                            this,
                            headerSize,
                            family,
                            isFrame ? FrameShowHeader : ShowHeader,
                            isFrame ? FrameShowUnlockTime : ShowUnlockTime,
                            isFrame ? FrameShowHeaderDateSeparator : ShowHeaderDateSeparator,
                            !isFrame && ShowFriendAvatar));
                        break;
                    case NotificationSurfaceStyle.LineTitle:
                        lines.Add(new ToastTitleLine(
                            this,
                            titleSize,
                            family,
                            isFrame ? FrameShowName : ShowName,
                            isFrame ? FrameTitleBrush : TitleBrush));
                        break;
                    case NotificationSurfaceStyle.LineDescription:
                        lines.Add(new ToastDescriptionLine(
                            this,
                            bodySize,
                            family,
                            isFrame ? FrameShowDescription : ShowDescription));
                        break;
                    case NotificationSurfaceStyle.LineGameCategory:
                        lines.Add(new ToastGameCategoryLine(
                            this,
                            gameCategorySize,
                            family,
                            isFrame ? FrameShowGameName : ShowGameName,
                            isFrame ? FrameShowCategory : ShowCategory,
                            isFrame ? FrameShowGameCategorySeparator : ShowGameCategorySeparator));
                        break;
                }
            }

            return lines;
        }

        private static double ResolveFontSizeResource(string key, double fallback)
        {
            return Application.Current?.TryFindResource(key) is double size && size > 0
                ? size
                : fallback;
        }

        private static FontFamily ResolveFontFamily(string familyName)
        {
            if (!string.IsNullOrWhiteSpace(familyName))
            {
                try
                {
                    return new FontFamily(familyName.Trim());
                }
                catch (ArgumentException)
                {
                    // Malformed persisted family name; fall through to the theme family.
                }
            }

            return Application.Current?.TryFindResource("PlayAch.FontFamily.Body") as FontFamily
                ?? SystemFonts.MessageFontFamily;
        }

        private static string NullIfBlank(string value) =>
            string.IsNullOrWhiteSpace(value) ? null : value;

        /// <summary>
        /// Synchronously decodes a user image for offscreen frame composition; null when the
        /// path is unset, missing, or unreadable so the drawn badge remains the fallback.
        /// </summary>
        private static ImageSource LoadSyncImage(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// UniPlaySong URI segment for this unlock's tier (e.g. "rareachievement"). Capstone and
        /// the completion notification take precedence over rarity; otherwise the rarity tier is
        /// used.
        /// </summary>
        public string SoundTierSegment
        {
            get
            {
                if (IsCapstone || IsGameCompleted)
                {
                    return "capstoneachievement";
                }

                switch (_rarity)
                {
                    case RarityTier.UltraRare:
                        return "ultrarareachievement";
                    case RarityTier.Rare:
                        return "rareachievement";
                    case RarityTier.Uncommon:
                        return "uncommonachievement";
                    default:
                        return "commonachievement";
                }
            }
        }

        /// <summary>
        /// Rarity ranking used to pick a single representative sound when several unlocks show at
        /// once. Higher is rarer; capstone and the completion notification outrank all rarity
        /// tiers.
        /// </summary>
        public int SoundTierRank
        {
            get
            {
                if (IsCapstone || IsGameCompleted)
                {
                    return 5;
                }

                switch (_rarity)
                {
                    case RarityTier.UltraRare:
                        return 4;
                    case RarityTier.Rare:
                        return 3;
                    case RarityTier.Uncommon:
                        return 2;
                    default:
                        return 1;
                }
            }
        }

        private static string MapTrophyKey(string trophyType)
        {
            switch (trophyType?.Trim().ToLowerInvariant())
            {
                case "platinum":
                    return "TrophyPlatinum";
                case "gold":
                    return "TrophyGold";
                case "silver":
                    return "TrophySilver";
                default:
                    return "TrophyBronze";
            }
        }

        private static RarityTier ParseRarity(string value)
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                Enum.TryParse(value, ignoreCase: true, result: out RarityTier rarity))
            {
                return rarity;
            }

            return RarityTier.Common;
        }
    }
}
