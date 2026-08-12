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
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.ViewModels
{
    public sealed class AchievementToastViewModel
    {
        private const string DefaultIcon =
            "pack://application:,,,/PlayniteAchievements;component/Resources/UnlockedAchIcon.png";

        // The rarity badge renders at this multiple of the title font size, in every rarity
        // display mode. Shared by the inline (title-line) badge and the footer badge so a badge
        // is the same size whether it sits inline before the name or in the icon column.
        public const double BadgeToTitleRatio = 1.25;

        // The right-side rarity badge (which replaces the provider icon) renders larger, at this
        // multiple of the title font size.
        public const double RightBadgeToTitleRatio = 2.5;

        // Frame font fallbacks are 1080-reference-canvas DIPs matching the bundled frame
        // template's historical literals, deliberately independent of theme font sizes.
        // Public (with the size fallbacks below) so the settings editor's sliders can rest on
        // the same defaults the renderer applies.
        public const double FrameHeaderFontFallback = 17;
        public const double FrameTitleFontFallback = 30;
        private const double FrameBodyFontFallback = 19;
        private const double FrameGameCategoryFontFallback = 19;

        // Built-in size fallbacks used when the style stores null.
        public const double DefaultToastIconSize = 55;
        public const double DefaultFrameIconSize = 84;
        public const double DefaultToastProviderIconSize = 24;
        public const double DefaultFrameProviderIconSize = 40;
        public const double DefaultToastCardWidth = 410;
        public const double DefaultToastTitleFontSize = 16;
        public const double DefaultToastCaptionFontSize = 11;

        private readonly AchievementUnlockedEventArgs _args;
        private readonly PersistedSettings _settings;
        private readonly NotificationStyleSettings _style;
        private readonly RarityTier _rarity;
        private IReadOnlyList<ToastLineDescriptor> _toastLines;
        private IReadOnlyList<ToastLineDescriptor> _frameLines;
        private ToastRarityTextLine _toastRarityText;
        private ToastRarityTextLine _frameRarityText;

        public AchievementToastViewModel(
            AchievementUnlockedEventArgs args,
            PersistedSettings settings,
            NotificationStyleSettings styleOverride = null,
            GameCustomDataStore gameCustomDataStore = null,
            bool? toastUseThemeStylingOverride = null,
            bool? frameUseThemeStylingOverride = null)
        {
            _args = args ?? new AchievementUnlockedEventArgs();
            _settings = settings ?? new PersistedSettings();
            var resolved = NotificationStyleResolver.ResolveAppearance(
                _settings,
                _args.ProviderKey,
                _args.PlayniteGameId,
                gameCustomDataStore);
            _style = styleOverride ?? resolved.Style;
            ToastUseThemeStyling =
                toastUseThemeStylingOverride ?? resolved.ToastUseThemeStyling;
            FrameUseThemeStyling =
                frameUseThemeStylingOverride ?? resolved.FrameUseThemeStyling;
            _rarity = ParseRarity(_args.RarityTier);
        }

        public bool ToastUseThemeStyling { get; }

        public bool FrameUseThemeStyling { get; }

        public bool IsFriendUnlock => _args.IsFriendUnlock;

        // Provider identity, bindable so a single toast/frame template can restyle per provider
        // with DataTriggers (e.g. trigger on ProviderKey, tint with ProviderColorHex).
        public string ProviderKey => _args.ProviderKey;
        public string ProviderName => Providers.ProviderRegistry.GetLocalizedName(ProviderKey);
        public string ProviderColorHex => Providers.ProviderRegistry.GetProviderColorHex(ProviderKey);

        // Raw fields consumed by the unlock-screenshot feature (not shown in the toast UI).
        public bool IsPreview => _args.IsPreview;

        // Real manual fire from the test-notification hotkey: capture still runs, but is routed
        // to a separate "Test" folder. Read by the notification service's screenshot planner.
        public bool IsTestFire => _args.IsTestFire;

        // Fire-test preview only: forces the template source for this notification. Null for
        // real unlocks. Read by the notification service when resolving the wave's template.
        internal Services.UI.NotificationTemplatePreviewSource? PreviewTemplateSource => _args.PreviewTemplateSource;
        internal string AchievementName => ResolveAchievementName(_args);
        internal int AchievementNumber => _args.AchievementNumber;

        /// <summary>
        /// Whether this unlock is being cut into a clip, so its card must be realized and sampled
        /// into an overlay track even when nothing about it shows on screen. Stamped at enqueue
        /// from the recording service's own eligibility check, at the same moment that service
        /// creates the clip request, so the two cannot disagree.
        /// </summary>
        internal bool NeedsOverlayTrack { get; set; }

        internal Guid CaptureCorrelationId => _args.CaptureCorrelationId;

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
        // Footer percent (under the achievement icon). Suppressed when the percent is set to
        // travel under the right-side badge instead.
        public bool ShowPercent => _style.Toast.ShowRarityPercent && _args.GlobalPercent.HasValue && !ShowRightPercent;
        public bool IsCapstone => _args.IsCapstone;

        /// <summary>
        /// True for the standalone "Congratulations! Game Complete!" notification that follows
        /// the completing unlock's wave. Regular unlocks — including the completion achievement
        /// itself — report false.
        /// </summary>
        public bool IsGameCompleted => _args.IsGameCompleted;

        /// <summary>
        /// When true, the live desktop toast's rarity/completed glow gently fades in and out.
        /// Mirrors the global display setting.
        /// </summary>
        public bool AnimateRarityGlows => _settings.AnimateRarityGlows;

        /// <summary>
        /// Whether this unlock's tier is one of the tiers selected for the soft halo. The card's outer
        /// border glow is unaffected — it is not a per-tier effect.
        /// </summary>
        public bool HasSoftGlowTier => _settings.RarityGlowSoftTiers.Contains(_rarity);

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
        private bool HasBadgeData => IsCapstone || HasTrophy || HasRarityData;
        public bool ShowBadge => _style.Toast.ShowRarityBadge && !_style.Toast.RightRarityBadge && HasBadgeData;

        // Whether the icon-column footer (badge and/or percent under the icon) shows anything.
        // Drives the footer container's visibility so it collapses cleanly and the icon-centering
        // spacer mirrors zero height when there is no footer.
        public bool HasIconFooter => ShowBadge || ShowPercent;

        // The rarity/trophy badge drawn inline before the achievement name (an alternative to
        // the icon-column footer badge). Shares the same badge image sources.
        public bool ShowInlineBadge => _style.Toast.InlineRarityBadge && HasBadgeData;
        public bool FrameShowInlineBadge => _style.Frame.InlineRarityBadge && HasBadgeData;

        // The rarity/trophy badge drawn larger on the right, replacing the provider icon. Shares
        // the same badge image sources as the footer/inline badges.
        public bool ShowRightBadge => _style.Toast.RightRarityBadge && HasBadgeData;
        public bool FrameShowRightBadge => _style.Frame.RightRarityBadge && HasBadgeData;

        // Percent rendered under the right-side badge: only when the badge is on the right and the
        // percent is set to travel with the badge. Otherwise the percent stays in the footer.
        public bool ShowRightPercent => _style.Toast.ShowRarityPercent && _style.Toast.RarityPercentUnderBadge
            && _style.Toast.RightRarityBadge && _args.GlobalPercent.HasValue;
        public bool FrameShowRightPercent => _style.Frame.ShowRarityPercent && _style.Frame.RarityPercentUnderBadge
            && _style.Frame.RightRarityBadge && _args.GlobalPercent.HasValue;
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
        public bool FrameShowPercent => _style.Frame.ShowRarityPercent && _args.GlobalPercent.HasValue && !FrameShowRightPercent;
        public bool FrameShowBadge => _style.Frame.ShowRarityBadge && !_style.Frame.RightRarityBadge && (IsCapstone || HasTrophy || HasRarityData);
        public bool FrameShowGameName => _style.Frame.ShowGameName && !string.IsNullOrWhiteSpace(_args.GameName);
        public bool FrameShowGameCategorySeparator => FrameShowGameName && FrameShowCategory;
        public bool FrameShowShineBorder => _style.Frame.ShowRarityGlow && HardcoreTakesBorder;

        // Frame vignette chrome: the radial edge vignette shows only for Full; the bottom contrast
        // wash shows for Full and Bottom. None removes both, leaving the raw screenshot.
        public bool FrameShowRadialVignette => _style.Frame.FrameVignette == FrameVignetteStyle.Full;
        public bool FrameShowBottomWash => _style.Frame.FrameVignette != FrameVignetteStyle.None;

        // Mirrors TitleBrush but honors the frame's own rarity-colored-name toggle.
        public Brush FrameTitleBrush => _style.Frame.RarityColoredName
            ? AccentBrush
            : Application.Current?.TryFindResource("PlayAch.Brush.Text") as Brush ?? Brushes.White;

        public Effect FrameRarityGlowEffect => _style.Frame.ShowRarityGlow && !HardcoreTakesBorder
            ? RarityAppearanceHelper.GetGlow(_rarity, 20, _settings)
            : null;

        // Header texts honor the surface's user edits with the localized strings as fallback.
        // Stored friend formats that lost their {0} placeholder fall back to the localized
        // default rather than crashing the toast. These Parent-level properties resolve from
        // the toast surface for custom/theme templates that bind Parent.HeaderText; the
        // bundled line templates bind the per-surface strings resolved onto the header line
        // descriptor instead.
        public string HeaderText => ResolveHeaderText(_style.Toast.HeaderTexts);

        /// <summary>
        /// Header text of the standalone game-completion notification ("Congratulations!" by
        /// default), honoring the toast surface's user edit.
        /// </summary>
        public string CompletionHeaderText => ResolveCompletionHeaderText(_style.Toast.HeaderTexts);

        /// <summary>
        /// Header text of a friend's game-completion notification ("{friend} completed the
        /// game!" by default), honoring the toast surface's user edit.
        /// </summary>
        public string FriendCompletionHeaderText => ResolveFriendCompletionHeaderText(_style.Toast.HeaderTexts);

        private string ResolveHeaderText(NotificationHeaderTextSettings texts)
        {
            if (IsFriendUnlock)
            {
                var format = NotificationHeaderTextService.IsValidHeaderFormat(texts.FriendUnlockHeaderFormat)
                    ? texts.FriendUnlockHeaderFormat
                    : ResourceProvider.GetString("LOCPlayAch_Toast_FriendUnlocked");
                return string.Format(format, FriendDisplayName);
            }

            return !string.IsNullOrWhiteSpace(texts.UnlockHeader)
                ? texts.UnlockHeader
                : ResourceProvider.GetString("LOCPlayAch_Toast_AchievementUnlocked");
        }

        private static string ResolveCompletionHeaderText(NotificationHeaderTextSettings texts) =>
            !string.IsNullOrWhiteSpace(texts.CompletionHeader)
                ? texts.CompletionHeader
                : ResourceProvider.GetString("LOCPlayAch_Toast_Congratulations");

        private string ResolveFriendCompletionHeaderText(NotificationHeaderTextSettings texts)
        {
            var format = NotificationHeaderTextService.IsValidHeaderFormat(texts.FriendCompletionHeaderFormat)
                ? texts.FriendCompletionHeaderFormat
                : "{0} " + ResourceProvider.GetString("LOCPlayAch_Toast_CompletedTheGame");
            return string.Format(format, FriendDisplayName);
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
        // Countdown timer bar fill: the user's custom color when set, else the default
        // progress-fill brush. Toast surface only.
        public Brush CountdownBarBrush
        {
            get
            {
                var color = _style.Toast.CountdownBarColor;
                if (!string.IsNullOrWhiteSpace(color))
                {
                    try
                    {
                        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
                        brush.Freeze();
                        return brush;
                    }
                    catch
                    {
                        // Fall through to the default brush on a malformed stored color.
                    }
                }

                return Application.Current?.TryFindResource("PlayAch.Brush.Progress.Fill") as Brush
                       ?? Brushes.Gray;
            }
        }

        public Brush AccentBrush => IsCapstone
            ? RarityAppearanceHelper.GetCompletedBrush(_settings)
            : RarityAppearanceHelper.GetBrush(_rarity, _settings);

        // Completion palette, always available regardless of this notification's kind so the
        // bundled templates (and themes) apply completion styling with triggers on
        // IsGameCompleted / IsCompletionAchievement. The glows honor the rarity-glow toggles.
        public Brush CompletedBrush => RarityAppearanceHelper.GetCompletedBrush(_settings);
        public Effect CompletedGlowEffect => _style.Toast.ShowRarityGlow && HasSoftCompletionGlow
            ? RarityAppearanceHelper.GetCompletedGlow(useEndColor: true, _settings)
            : null;
        public Effect FrameCompletedGlowEffect => _style.Frame.ShowRarityGlow && HasSoftCompletionGlow
            ? RarityAppearanceHelper.GetCompletedGlow(useEndColor: true, _settings)
            : null;

        /// <summary>
        /// The completion bloom's other half. Game and category art carry both of these on stacked
        /// copies, one thrown from each corner, and it is the pair that reads as the completion
        /// gradient — a single copy is just a coloured halo. The notification surfaces had only the end
        /// colour, so completion looked like a flat glow there rather than the gradient everywhere else.
        /// </summary>
        public Effect CompletedGlowStartEffect => _style.Toast.ShowRarityGlow && HasSoftCompletionGlow
            ? RarityAppearanceHelper.GetCompletedGlow(useEndColor: false, _settings)
            : null;

        public Effect FrameCompletedGlowStartEffect => _style.Frame.ShowRarityGlow && HasSoftCompletionGlow
            ? RarityAppearanceHelper.GetCompletedGlow(useEndColor: false, _settings)
            : null;

        /// <summary>
        /// Completion counterpart to <see cref="RarityEdgeEffect"/>. Without it the edge on a completion
        /// notification kept the rarity colour, which for the completion event is whatever tier the
        /// unlock behind it carried — so the edge came out ultra rare beside a completion-coloured glow.
        /// </summary>
        public Effect CompletedEdgeEffect => ShowRayBurst
            ? RarityAppearanceHelper.GetCompletedEdge(_settings)
            : null;

        public Effect FrameCompletedEdgeEffect => FrameShowRayBurst
            ? RarityAppearanceHelper.GetCompletedEdge(_settings)
            : null;

        /// <summary>
        /// Whether the completed-game halo is selected. Completion is its own entry in the glow
        /// selections rather than a rarity tier, because the completion notification carries no rarity.
        /// </summary>
        private bool HasSoftCompletionGlow => _settings.RarityGlowSoftTiers.IncludesCompleted();
        public ImageSource CompletedBadgeImage => RarityAppearanceHelper.CreateCompletedBadgePreview(_settings);
        public Brush RarityBrush => RarityAppearanceHelper.GetBrush(_rarity, _settings);

        // Capstone color takes precedence over rarity, matching the grid's RarityNameBrush.
        public Brush TitleBrush => _style.Toast.RarityColoredName
            ? AccentBrush
            : Application.Current?.TryFindResource("PlayAch.Brush.Text") as Brush ?? Brushes.White;

        // Title color for the standalone "Game Complete!" notification. Honors each surface's
        // rarity-colored-name toggle exactly like TitleBrush/FrameTitleBrush do for a normal unlock:
        // the completed color when on, plain text when off.
        public Brush CompletedTitleBrush => _style.Toast.RarityColoredName ? CompletedBrush : PlainTitleBrush;
        public Brush FrameCompletedTitleBrush => _style.Frame.RarityColoredName ? CompletedBrush : PlainTitleBrush;
        private static Brush PlainTitleBrush =>
            Application.Current?.TryFindResource("PlayAch.Brush.Text") as Brush ?? Brushes.White;

        public bool IsHardcore => _args.IsHardcore;

        /// <summary>
        /// Whether this unlock takes the crisp Hardcore border in place of a glow. Hardcore only
        /// claims the border while the user leaves that behavior on; with it off, a Hardcore unlock is
        /// glowed like any other, following its rarity tier.
        /// </summary>
        private bool HardcoreTakesBorder => IsHardcore && _settings.ShowHardcoreBorder;

        /// <summary>
        /// Hardcore RetroAchievements unlocks get a crisp rarity-colored border in place of the
        /// soft glow, mirroring the datagrids. Both are gated on the rarity-glow toggle.
        /// </summary>
        public bool ShowShineBorder => _style.Toast.ShowRarityGlow && HardcoreTakesBorder;

        // Glossy metallic rarity border (matches RarityToShineBrush used by the datagrids).
        public Brush IconBorderBrush => RarityAppearanceHelper.GetShineBrush(_rarity, _settings);

        // Soft rarity glow for non-hardcore unlocks whose tier is selected for it (matches the
        // datagrids' glow, BlurRadius 20).
        public Effect RarityGlowEffect => _style.Toast.ShowRarityGlow && !HardcoreTakesBorder && HasSoftGlowTier
            ? RarityAppearanceHelper.GetGlow(_rarity, 20, _settings)
            : null;

        /// <summary>
        /// True when the notification icon carries the rotating sunburst behind its soft halo: this
        /// unlock's tier is selected for rays, the surface shows rarity glows, and the unlock is not
        /// Hardcore (which keeps its crisp border instead of any glow). The two effects are selected
        /// independently, so a tier can have rays without the halo or the other way round. Gating this
        /// here keeps the template markup to a single binding.
        /// </summary>
        public bool ShowRayBurst =>
            HasRaySelection &&
            _style.Toast.ShowRarityGlow &&
            !HardcoreTakesBorder;

        /// <summary>
        /// Edge that comes with the rays: the same drop shadow as the soft halo, at a blur small enough
        /// to read as a line along the artwork rather than a glow around it. It follows the alpha
        /// because it is a blur of the picture itself, which is the only way to hug cut-out art.
        /// </summary>
        public Effect RarityEdgeEffect => ShowRayBurst
            ? RarityAppearanceHelper.GetGlow(_rarity, RayEdgeBlurRadius, _settings)
            : null;

        /// <summary>Screenshot-frame counterpart to <see cref="RarityEdgeEffect"/>.</summary>
        public Effect FrameRarityEdgeEffect => FrameShowRayBurst
            ? RarityAppearanceHelper.GetGlow(_rarity, RayEdgeBlurRadius, _settings)
            : null;

        /// <summary>Matches the ConverterParameter the grid templates pass for the same edge.</summary>
        private const double RayEdgeBlurRadius = 4;

        /// <summary>Screenshot-frame counterpart to <see cref="ShowRayBurst"/>.</summary>
        public bool FrameShowRayBurst =>
            HasRaySelection &&
            _style.Frame.ShowRarityGlow &&
            !HardcoreTakesBorder;

        /// <summary>
        /// Whether the rays are selected for whatever this notification is about. The completion
        /// notification is matched against the selection's completion entry rather than a rarity tier,
        /// since it carries no rarity of its own.
        /// </summary>
        private bool HasRaySelection => IsGameCompleted
            ? _settings.RarityGlowRayTiers.IncludesCompleted()
            : _settings.RarityGlowRayTiers.Contains(_rarity);

        // Rarity-colored glow on the toast card border (replaces the default drop shadow when
        // the border-glow option is on). Toast surface only. Completion uses the completed glow.
        public bool HasBorderGlow => _style.Toast.NotificationBorderGlow;

        // The card border glow is larger than the icon glow (blur 20) so it reads as a halo
        // around the whole card.
        private const double BorderGlowBlurRadius = 36;

        // Room reserved around the card for its shadow/glow so nothing clips it, everywhere (live
        // toast and previews alike): enough for the wide border glow when it is on, otherwise the
        // neutral drop shadow. Derived from the glow radius, so bumping the glow needs no synced
        // constant. This margin lives inside the toast window, so the glow stays within the window
        // (never clipped by it) and the window is placed with a gap from the screen edge, keeping
        // the whole glow on-screen — the card simply sits a little further in, which is intended.
        /// <summary>
        /// Transparent room around the card for whatever reaches past it. The window is sized to this,
        /// so anything drawn beyond it is simply cut off.
        ///
        /// The rays reach furthest, and by more than the shadow does: their length scales with the
        /// subject, and the card is far larger than an icon. Their share is worked out the same way the
        /// layout does it, rather than guessed, so changing the burst scale in the template cannot
        /// silently start clipping them.
        /// </summary>
        public Thickness ToastGlowMargin
        {
            get
            {
                var glow = HasBorderGlow ? BorderGlowBlurRadius + 6 : 16;
                if (!ShowRayBurst)
                {
                    return new Thickness(glow);
                }

                var width = ToastCardWidth > 0 ? ToastCardWidth : DefaultToastCardWidth;
                var height = ToastCardHeight > 0 ? ToastCardHeight : 96;
                var reach = (ToastCardBurstScale - 1.0) * 0.5 * Math.Sqrt(width * height);
                return new Thickness(Math.Max(glow, reach + 6));
            }
        }

        /// <summary>Kept in step with the BurstScale the bundled toast template passes.</summary>
        private const double ToastCardBurstScale = 1.14;


        // Cloned to an unfrozen copy so the card's border-glow pulse can animate its Opacity
        // (the shared GetGlow/GetCompletedGlow instances are frozen and immutable), and so its
        // BlurRadius can be widened to the border-glow radius. Null for Common rarity (no glow).
        public Effect BorderGlowEffect
        {
            get
            {
                if (!HasBorderGlow)
                {
                    return null;
                }

                var glow = IsGameCompleted
                    ? RarityAppearanceHelper.GetCompletedGlow(useEndColor: true, _settings)?.Clone()
                    : RarityAppearanceHelper.GetGlow(_rarity, BorderGlowBlurRadius, _settings)?.Clone();
                if (glow is DropShadowEffect dropShadow)
                {
                    dropShadow.BlurRadius = BorderGlowBlurRadius;
                }

                return glow;
            }
        }

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
        /// User badge image for this unlock's badge slot in the given surface's set, or null
        /// for the drawn badge. Capstones use the completion slot; otherwise a set rarity
        /// image wins over the trophy badge (trophy-typed unlocks carry rarity data too),
        /// which is the documented custom-rarity-beats-trophy rule.
        /// </summary>
        private string ResolveCustomBadgePath(NotificationBadgeImageSet badges)
        {
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

        // Badge source for the toast template's AsyncImage binding: the toast surface's custom
        // image path when one is set (a string, so animated GIF badges animate), otherwise the
        // drawn badge ImageSource. Cache-busted (write-time + size token, stripped before
        // decoding) so an overwritten badge file at the same managed slot path never shows a
        // stale cached bitmap.
        public object ToastBadgeSource =>
            (object)AchievementIconResolver.ApplyCacheBust(ResolveCustomBadgePath(_style.Toast.BadgeImages)) ?? BadgeImage;

        // Toast icon-swap source for the completion trigger, mirroring CompletedBadgeImage.
        public object ToastCompletedBadgeSource =>
            (object)AchievementIconResolver.ApplyCacheBust(NullIfBlank(_style.Toast.BadgeImages.CompletionPath)) ?? CompletedBadgeImage;

        // Frame equivalents read the frame surface's own badge set. The frame is rendered
        // offscreen, so images must be synchronously decoded (an async load renders blank);
        // animated GIFs contribute their first frame.
        public ImageSource FrameBadgeImage =>
            LoadSyncImage(ResolveCustomBadgePath(_style.Frame.BadgeImages)) ?? BadgeImage;

        public ImageSource FrameCompletedBadgeImage =>
            LoadSyncImage(NullIfBlank(_style.Frame.BadgeImages.CompletionPath)) ?? CompletedBadgeImage;

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

        /// <summary>
        /// The provider (platform) icon as a ready-to-bind, provider-tinted <see cref="ImageSource"/>,
        /// so theme and custom templates can bind <c>Image.Source="{Binding ProviderIcon}"</c>
        /// directly without needing the ProviderIconConverter. Null when the provider is unknown.
        /// Loads synchronously (a DrawingImage), so it is safe for the offscreen frame render too.
        /// </summary>
        public ImageSource ProviderIcon =>
            Views.Converters.ProviderIconConverter.BuildIcon(ProviderIconKey, ProviderColorHex);

        // The right-side badge replaces the provider icon, so the icon is hidden while it is on.
        public bool ShowProviderIcon => _style.Toast.ShowProviderIcon && !_style.Toast.RightRarityBadge && !string.IsNullOrWhiteSpace(ProviderIconKey);
        public bool FrameShowProviderIcon => _style.Frame.ShowProviderIcon && !_style.Frame.RightRarityBadge && !string.IsNullOrWhiteSpace(ProviderIconKey);

        // Left rarity accent strip and bottom countdown bar (toast only; hiding the bar does
        // not change auto-dismiss timing).
        public bool ShowAccentStrip => _style.Toast.ShowAccentStrip;
        public bool ShowCountdownBar => _style.Toast.ShowCountdownBar;

        // User left/right padding for the toast card content (keeps the background image full-bleed).
        public Thickness ToastContentPadding =>
            new Thickness(_style.Toast.CardPaddingLeft ?? 0, 0, _style.Toast.CardPaddingRight ?? 0, 0);

        // User toast card dimensions, falling back to the bundled template's defaults.
        public double ToastCardWidth => _style.Toast.CardWidth is double w && w > 0 ? w : DefaultToastCardWidth;

        // NaN maps to WPF "Auto": with no explicit height the card sizes to its content (its
        // natural height), floored by the template's MinHeight. A set value fixes the height.
        public double ToastCardHeight => _style.Toast.CardHeight is double h && h > 0 ? h : double.NaN;

        // User toast background image (frames never get a background). Missing files fall
        // back to the default surface brush via HasToastBackground. Cache-busted (write-time +
        // size token) so overwriting the image at the same managed path shows the new one rather
        // than a stale cached bitmap; AsyncImage strips the token before decoding.
        public string ToastBackgroundImagePath => AchievementIconResolver.ApplyCacheBust(_style.ToastBackgroundImagePath);
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

        // Effective caption/header size per surface.
        public double ToastHeaderFontSize => _style.Toast.HeaderFontSize
            ?? ResolveFontSizeResource("PlayAch.FontSize.Caption", DefaultToastCaptionFontSize);
        public double FrameHeaderFontSize => _style.Frame.HeaderFontSize ?? FrameHeaderFontFallback;

        // Effective rarity percent text size per surface. Decoupled from the header size; when
        // unset it falls back to the same caption/header default so the out-of-the-box look is
        // unchanged.
        public double ToastRarityFontSize => _style.Toast.RarityFontSize
            ?? ResolveFontSizeResource("PlayAch.FontSize.Caption", DefaultToastCaptionFontSize);
        public double FrameRarityFontSize => _style.Frame.RarityFontSize ?? FrameHeaderFontFallback;

        // Effective title size per surface: the single source of truth for both the title line
        // and the badge size, so the inline and footer badges always match.
        public double ToastTitleFontSize => _style.Toast.TitleFontSize
            ?? ResolveFontSizeResource("PlayAch.FontSize.Title", DefaultToastTitleFontSize);
        public double FrameTitleFontSize => _style.Frame.TitleFontSize ?? FrameTitleFontFallback;

        // Rarity badge render size per surface, identical across every rarity display mode
        // (inline badge and footer badge both bind to this). A user RarityBadgeSize overrides the
        // computed size everywhere the badge appears.
        public double ToastBadgeSize => _style.Toast.RarityBadgeSize ?? (ToastTitleFontSize * BadgeToTitleRatio);
        public double FrameBadgeSize => _style.Frame.RarityBadgeSize ?? (FrameTitleFontSize * BadgeToTitleRatio);

        // Right-side badge render size per surface (larger by default; it stands in for the
        // provider icon). A user RarityBadgeSize applies here too, so one setting controls the
        // badge size no matter where it sits.
        public double ToastRightBadgeSize => _style.Toast.RarityBadgeSize ?? (ToastTitleFontSize * RightBadgeToTitleRatio);
        public double FrameRightBadgeSize => _style.Frame.RarityBadgeSize ?? (FrameTitleFontSize * RightBadgeToTitleRatio);

        // Achievement icon render size per surface (user IconSize, else the bundled default).
        public double ToastIconSize => _style.Toast.IconSize is double s && s > 0 ? s : DefaultToastIconSize;
        public double FrameIconSize => _style.Frame.IconSize is double s && s > 0 ? s : DefaultFrameIconSize;

        // Provider (platform) icon render size per surface (user ProviderIconSize, else default).
        public double ToastProviderIconSize => _style.Toast.ProviderIconSize is double s && s > 0 ? s : DefaultToastProviderIconSize;
        public double FrameProviderIconSize => _style.Frame.ProviderIconSize is double s && s > 0 ? s : DefaultFrameProviderIconSize;

        // The content shadow is directional (down-right) with a FIXED, tight blur: widening
        // the blur is what smears a whole text line's shadow into a straight-edged band. Two
        // independent settings drive it: opacity darkens the shadow (stacking a second layer
        // through its upper half, up to solid black), and offset moves it away from the
        // glyphs (as a fraction of the maximum depths below).
        private const double ContentShadowBlur = 5;
        private const double ContentShadowInnerBlur = 2.5;
        private const double ContentShadowMaxDepth = 4;
        private const double ContentShadowInnerMaxDepth = 3;

        // The percent values that map to the built-in shadow; shared with the settings editor.
        public const double DefaultTextShadowOpacity = 50;
        public const double DefaultTextShadowOffset = 25;

        // Image shadows are single-layer (artwork has no thin glyph edges to solidify), so
        // 100 is both the default and the darkest the layer can go.
        public const double DefaultImageShadowOpacity = 100;
        public const double DefaultImageShadowOffset = 25;

        /// <summary>
        /// The drop shadow behind this surface's text, badges, and logos, shaped by the
        /// surface's shadow opacity and offset settings; null when disabled (opacity 0).
        /// </summary>
        public Effect ToastContentShadow =>
            BuildContentShadow(_style.Toast.TextShadowOpacity, _style.Toast.TextShadowOffset);

        public Effect FrameContentShadow =>
            BuildContentShadow(_style.Frame.TextShadowOpacity, _style.Frame.TextShadowOffset);

        /// <summary>
        /// Second shadow layer for the text-line templates. Null at and below the built-in
        /// opacity (the single-layer look is unchanged there); above it, a tight second
        /// directional shadow fades in under the outer one, so the top of the range reaches
        /// solid black — a single gaussian layer cannot exceed its feathered opacity.
        /// </summary>
        public Effect ToastContentShadowInner =>
            BuildInnerContentShadow(_style.Toast.TextShadowOpacity, _style.Toast.TextShadowOffset);

        public Effect FrameContentShadowInner =>
            BuildInnerContentShadow(_style.Frame.TextShadowOpacity, _style.Frame.TextShadowOffset);

        /// <summary>
        /// The drop shadow behind this surface's images (badge images and the provider icon),
        /// independent of the text shadow; null when disabled (opacity 0). The frame's
        /// branding block keeps the fixed built-in shadow instead.
        /// </summary>
        public Effect ToastImageShadow =>
            BuildImageShadow(_style.Toast.ImageShadowOpacity, _style.Toast.ImageShadowOffset);

        public Effect FrameImageShadow =>
            BuildImageShadow(_style.Frame.ImageShadowOpacity, _style.Frame.ImageShadowOffset);

        private static Effect BuildImageShadow(double? opacityPercent, double? offsetPercent)
        {
            var opacity = NormalizePercent(opacityPercent, DefaultImageShadowOpacity);
            if (opacity <= 0)
            {
                return null;
            }

            var effect = new DropShadowEffect
            {
                BlurRadius = ContentShadowBlur,
                ShadowDepth = ContentShadowMaxDepth * NormalizePercent(offsetPercent, DefaultImageShadowOffset),
                Direction = 315,
                Color = Colors.Black,
                Opacity = opacity
            };
            effect.Freeze();
            return effect;
        }

        private static double NormalizePercent(double? value, double fallback) =>
            Math.Max(0.0, Math.Min(100.0, value ?? fallback)) / 100.0;

        private static Effect BuildContentShadow(double? opacityPercent, double? offsetPercent)
        {
            var darkness = NormalizePercent(opacityPercent, DefaultTextShadowOpacity);
            if (darkness <= 0)
            {
                return null;
            }

            var effect = new DropShadowEffect
            {
                BlurRadius = ContentShadowBlur,
                ShadowDepth = ContentShadowMaxDepth * NormalizePercent(offsetPercent, DefaultTextShadowOffset),
                Direction = 315,
                Color = Colors.Black,
                // Fully dark from the built-in opacity (50%) upward; the layer stacking
                // below carries the upper half of the range.
                Opacity = Math.Min(1.0, darkness * 2.0)
            };
            effect.Freeze();
            return effect;
        }

        private static Effect BuildInnerContentShadow(double? opacityPercent, double? offsetPercent)
        {
            var darkness = NormalizePercent(opacityPercent, DefaultTextShadowOpacity);
            if (darkness <= 0.5)
            {
                return null;
            }

            // 0..1 across the upper half of the opacity range; solid black by 75%.
            var extra = (darkness - 0.5) * 2.0;
            var effect = new DropShadowEffect
            {
                BlurRadius = ContentShadowInnerBlur,
                ShadowDepth = ContentShadowInnerMaxDepth * NormalizePercent(offsetPercent, DefaultTextShadowOffset),
                Direction = 315,
                Color = Colors.Black,
                Opacity = Math.Min(1.0, extra * 2.0)
            };
            effect.Freeze();
            return effect;
        }

        /// <summary>
        /// The toast's text lines in the user's order; hidden lines are still present with
        /// their visibility flags false so completion triggers can force them visible.
        /// </summary>
        public IReadOnlyList<ToastLineDescriptor> ToastLines =>
            _toastLines ?? (_toastLines = BuildLines(isFrame: false));

        /// <summary>
        /// The frame's text lines in the user's order. Frame lines never show the friend
        /// avatar (friend unlocks do not produce screenshots).
        /// </summary>
        public IReadOnlyList<ToastLineDescriptor> FrameLines =>
            _frameLines ?? (_frameLines = BuildLines(isFrame: true));

        /// <summary>
        /// The toast's rarity percent font values (family, size, weight, style, decorations).
        /// </summary>
        public ToastRarityTextLine ToastRarityText =>
            _toastRarityText ?? (_toastRarityText = BuildRarityText(isFrame: false));

        /// <summary>
        /// The frame's rarity percent font values.
        /// </summary>
        public ToastRarityTextLine FrameRarityText =>
            _frameRarityText ?? (_frameRarityText = BuildRarityText(isFrame: true));

        private IReadOnlyList<ToastLineDescriptor> BuildLines(bool isFrame)
        {
            var surface = isFrame ? _style.Frame : _style.Toast;

            FontFamily LineFamily(string overrideFamily) => ResolveLineFontFamily(overrideFamily, isFrame);
            var headerSize = isFrame ? FrameHeaderFontSize : ToastHeaderFontSize;
            var titleSize = isFrame ? FrameTitleFontSize : ToastTitleFontSize;
            var bodySize = surface.BodyFontSize ??
                (isFrame ? FrameBodyFontFallback : ResolveFontSizeResource("PlayAch.FontSize.Caption", DefaultToastCaptionFontSize));
            var gameCategorySize = surface.GameCategoryFontSize ??
                (isFrame ? FrameGameCategoryFontFallback : ResolveFontSizeResource("PlayAch.FontSize.Caption", DefaultToastCaptionFontSize));

            var showGameName = isFrame ? FrameShowGameName : ShowGameName;
            var showCategory = isFrame ? FrameShowCategory : ShowCategory;
            // The description gives up its second line when a game/category line follows it, so the
            // two rows together stay within the card instead of overflowing.
            var descriptionMaxLines = (showGameName || showCategory) ? 1 : 2;

            // Name-line offset: a positive value indents the title line to the right; a negative
            // value indents every other line instead, so the title line (with its inline badge)
            // never slides left under the icon column. The standalone completion notification has
            // no inline badge (its title is "Game Complete!"), so the offset does not apply there.
            var offset = IsGameCompleted ? 0 : surface.TitleLineOffset;
            var titleIndent = offset > 0 ? offset : 0;
            var otherIndent = offset < 0 ? -offset : 0;

            // Extra top/bottom padding applied to every line.
            var linePadding = surface.LinePadding is double lp && lp > 0 ? lp : 0;

            // One strength-scaled shadow instance shared by every line of this surface.
            var contentShadow = isFrame ? FrameContentShadow : ToastContentShadow;

            var lines = new List<ToastLineDescriptor>(NotificationSurfaceStyle.DefaultLineOrder.Count);
            foreach (var token in NotificationSurfaceStyle.CanonicalizeLineOrder(surface.LineOrder))
            {
                switch (token)
                {
                    case NotificationSurfaceStyle.LineHeader:
                        lines.Add(new ToastHeaderLine(
                            this,
                            headerSize,
                            LineFamily(surface.HeaderFontFamily),
                            contentShadow,
                            isFrame ? FrameShowHeader : ShowHeader,
                            isFrame ? FrameShowUnlockTime : ShowUnlockTime,
                            isFrame ? FrameShowHeaderDateSeparator : ShowHeaderDateSeparator,
                            !isFrame && ShowFriendAvatar,
                            ResolveHeaderText(surface.HeaderTexts),
                            ResolveCompletionHeaderText(surface.HeaderTexts),
                            ResolveFriendCompletionHeaderText(surface.HeaderTexts)));
                        break;
                    case NotificationSurfaceStyle.LineTitle:
                        lines.Add(new ToastTitleLine(
                            this,
                            titleSize,
                            LineFamily(surface.TitleFontFamily),
                            contentShadow,
                            isFrame ? FrameShowName : ShowName,
                            isFrame ? FrameTitleBrush : TitleBrush,
                            isFrame ? FrameCompletedTitleBrush : CompletedTitleBrush,
                            isFrame ? FrameShowInlineBadge : ShowInlineBadge,
                            isFrame ? (object)FrameBadgeImage : ToastBadgeSource,
                            isFrame ? FrameBadgeSize : ToastBadgeSize));
                        break;
                    case NotificationSurfaceStyle.LineDescription:
                        lines.Add(new ToastDescriptionLine(
                            this,
                            bodySize,
                            LineFamily(surface.BodyFontFamily),
                            contentShadow,
                            isFrame ? FrameShowDescription : ShowDescription,
                            descriptionMaxLines));
                        break;
                    case NotificationSurfaceStyle.LineGameCategory:
                        lines.Add(new ToastGameCategoryLine(
                            this,
                            gameCategorySize,
                            LineFamily(surface.GameCategoryFontFamily),
                            contentShadow,
                            showGameName,
                            showCategory,
                            isFrame ? FrameShowGameCategorySeparator : ShowGameCategorySeparator));
                        break;
                }
            }

            var innerShadow = isFrame ? FrameContentShadowInner : ToastContentShadowInner;
            var imageShadow = isFrame ? FrameImageShadow : ToastImageShadow;
            var textBrush = Application.Current?.TryFindResource("PlayAch.Brush.Text") as Brush
                ?? Brushes.White;
            foreach (var line in lines)
            {
                line.LeftIndent = line is ToastTitleLine ? titleIndent : otherIndent;
                line.VerticalPadding = linePadding;
                line.ImageShadow = imageShadow;
                ApplyLineEmphasis(line, surface, innerShadow, textBrush);
            }

            // Only the bottom-most line that actually renders needs descender room: every line
            // above one overhangs into the next line box's empty top leading, which nothing clips.
            // Resolved here rather than assumed, because the line order is user-reorderable and any
            // line can collapse when it has no content.
            for (var i = lines.Count - 1; i >= 0; i--)
            {
                if (lines[i].LineVisibility == Visibility.Visible)
                {
                    lines[i].IsBottomLine = true;
                    break;
                }
            }

            return lines;
        }

        /// <summary>
        /// Resolves a line's font family: the line's own override wins over the surface family,
        /// which itself falls back to the theme-derived family.
        /// </summary>
        private FontFamily ResolveLineFontFamily(string overrideFamily, bool isFrame)
        {
            return string.IsNullOrWhiteSpace(overrideFamily)
                ? (isFrame ? FrameFontFamily : ToastFontFamily)
                : ResolveFontFamily(overrideFamily);
        }

        /// <summary>
        /// Applies a surface's per-line emphasis (bold/italic/underline/strikethrough) and the
        /// inner shadow layer onto a descriptor. Shared by the reorderable lines and the rarity
        /// percent so the percent honors the same toggles.
        /// </summary>
        private void ApplyLineEmphasis(
            ToastLineDescriptor line,
            NotificationSurfaceStyle surface,
            Effect innerShadow,
            Brush textBrush)
        {
            line.TextShadowInner = innerShadow;

            var emphasis = ResolveLineEmphasis(surface, line);
            // The title line's base weight is SemiBold, which fonts without a SemiBold
            // face (e.g. Trebuchet MS) already render with their Bold face — so its bold
            // toggle jumps all the way to Black, the farthest heavy face, to stay visible
            // across font families. WPF picks the nearest existing face per weight.
            line.FontWeight = (emphasis & NotificationLineEmphasis.Bold) != 0
                ? (line is ToastTitleLine ? FontWeights.Black : FontWeights.Bold)
                : (line is ToastTitleLine ? FontWeights.SemiBold : FontWeights.Normal);
            line.FontStyle = (emphasis & NotificationLineEmphasis.Italic) != 0
                ? FontStyles.Italic
                : FontStyles.Normal;
            line.TextDecorations = BuildLineDecorations(
                emphasis, LineDecorationBrush(line, textBrush));
        }

        /// <summary>
        /// Builds the rarity percent's resolved font values for a surface. Kept out of the line
        /// list: the templates draw the percent themselves and gate it on their own flags.
        /// </summary>
        private ToastRarityTextLine BuildRarityText(bool isFrame)
        {
            var surface = isFrame ? _style.Frame : _style.Toast;
            var line = new ToastRarityTextLine(
                this,
                isFrame ? FrameRarityFontSize : ToastRarityFontSize,
                ResolveLineFontFamily(surface.RarityFontFamily, isFrame),
                isFrame ? FrameContentShadow : ToastContentShadow);

            var textBrush = Application.Current?.TryFindResource("PlayAch.Brush.Text") as Brush
                ?? Brushes.White;
            ApplyLineEmphasis(
                line,
                surface,
                isFrame ? FrameContentShadowInner : ToastContentShadowInner,
                textBrush);
            return line;
        }

        /// <summary>
        /// The brush a line's underline/strikethrough pen should use: the title line draws in
        /// its (possibly rarity-colored) title brush, every other line in the shared text brush.
        /// </summary>
        private Brush LineDecorationBrush(ToastLineDescriptor line, Brush textBrush)
        {
            return line is ToastTitleLine title
                ? (IsGameCompleted ? title.CompletedTitleBrush : title.TitleBrush) ?? textBrush
                : textBrush;
        }

        private static NotificationLineEmphasis ResolveLineEmphasis(
            NotificationSurfaceStyle surface, ToastLineDescriptor line)
        {
            switch (line)
            {
                case ToastHeaderLine _:
                    return surface.HeaderEmphasis;
                case ToastTitleLine _:
                    return surface.TitleEmphasis;
                case ToastDescriptionLine _:
                    return surface.BodyEmphasis;
                case ToastGameCategoryLine _:
                    return surface.GameCategoryEmphasis;
                case ToastRarityTextLine _:
                    return surface.RarityEmphasis;
                default:
                    return NotificationLineEmphasis.None;
            }
        }

        // Decoration pen thickness as multiples of the font's recommended (hairline) thickness
        // (TextDecorationUnit.FontRecommended: the pen's Thickness value is a multiplier, and
        // the result scales with the font size automatically). The WPF default is 1x, which
        // reads far thinner than the text it marks; bold rows get a heavier line still.
        private const double DecorationThicknessMultiplier = 2.0;
        private const double BoldDecorationThicknessMultiplier = 3.0;

        private static TextDecorationCollection BuildLineDecorations(
            NotificationLineEmphasis emphasis, Brush brush)
        {
            var underline = (emphasis & NotificationLineEmphasis.Underline) != 0;
            var strike = (emphasis & NotificationLineEmphasis.Strikethrough) != 0;
            if (!underline && !strike)
            {
                return null;
            }

            var bold = (emphasis & NotificationLineEmphasis.Bold) != 0;
            var pen = new Pen(
                brush,
                bold ? BoldDecorationThicknessMultiplier : DecorationThicknessMultiplier);
            if (pen.CanFreeze)
            {
                pen.Freeze();
            }

            var decorations = new TextDecorationCollection();
            if (underline)
            {
                decorations.Add(new TextDecoration
                {
                    Location = TextDecorationLocation.Underline,
                    Pen = pen,
                    PenThicknessUnit = TextDecorationUnit.FontRecommended
                });
            }

            if (strike)
            {
                decorations.Add(new TextDecoration
                {
                    Location = TextDecorationLocation.Strikethrough,
                    Pen = pen,
                    PenThicknessUnit = TextDecorationUnit.FontRecommended
                });
            }

            if (decorations.CanFreeze)
            {
                decorations.Freeze();
            }

            return decorations;
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
                // Bypass WPF's process-wide bitmap cache: the managed slots reuse fixed file
                // names, so an overwritten file at the same path must decode fresh bytes.
                image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
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

        // UniPlaySong's URI segment for hidden achievements. Named because UniPlaySong may rename
        // it; the four rarity segments and capstone are inline in SoundTierSegment below.
        public const string HiddenSoundSegment = "hidden";

        /// <summary>
        /// Whether this unlock plays UniPlaySong's hidden-achievement sound: the achievement is
        /// hidden and the user opted into the hidden sound. Shared by
        /// <see cref="SoundTierSegment"/> and <see cref="SoundTierRank"/> so the two cannot
        /// disagree about which unlocks are hidden.
        /// </summary>
        private bool UseHiddenSound => _settings.UseHiddenUnlockSound && _args.IsHidden;

        /// <summary>
        /// UniPlaySong URI segment for this unlock's tier (e.g. "rareachievement"). The hidden
        /// sound takes precedence over everything when enabled, then capstone and the completion
        /// notification, and otherwise the rarity tier is used. Note this order is deliberately the
        /// inverse of <see cref="SoundTierRank"/>'s, which keeps capstone at the top: a hidden
        /// capstone plays the hidden sound while still ranking as a capstone in its wave.
        /// </summary>
        public string SoundTierSegment
        {
            get
            {
                if (UseHiddenSound)
                {
                    return HiddenSoundSegment;
                }

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
        /// once. Higher is rarer; capstone and the completion notification outrank everything, and
        /// a hidden unlock sits between them and the rarity tiers.
        /// </summary>
        public int SoundTierRank
        {
            get
            {
                if (IsCapstone || IsGameCompleted)
                {
                    return 6;
                }

                if (UseHiddenSound)
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
