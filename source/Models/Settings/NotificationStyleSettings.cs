using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace PlayniteAchievements.Models.Settings
{
    using ObservableObject = Common.ObservableObject;

    /// <summary>
    /// Appearance customization for the unlock notification surfaces: the on-screen toast and
    /// the composited screenshot frame. One instance is the global default; per-provider
    /// whole-style copies live in <see cref="PersistedSettings.ProviderNotificationStyles"/>.
    /// Behavior toggles (whether toasts/screenshots fire at all) remain in
    /// <see cref="ProviderNotificationOverride"/> and are unrelated to this class.
    /// </summary>
    public sealed class NotificationStyleSettings : ObservableObject
    {
        private NotificationSurfaceStyle _toast;
        private NotificationSurfaceStyle _frame;
        private string _toastBackgroundImagePath;

        /// <summary>
        /// Style for the on-screen toast surface. Lazily initialized; never null.
        /// </summary>
        public NotificationSurfaceStyle Toast
        {
            get => _toast ?? (_toast = NotificationSurfaceStyle.CreateToastDefault());
            set => SetValue(ref _toast, value);
        }

        /// <summary>
        /// Style for the screenshot frame surface. Lazily initialized; never null.
        /// </summary>
        public NotificationSurfaceStyle Frame
        {
            get => _frame ?? (_frame = NotificationSurfaceStyle.CreateFrameDefault());
            set => SetValue(ref _frame, value);
        }

        /// <summary>
        /// Absolute path of a user-supplied toast background image in any format ImageFormats
        /// recognizes, or null for the default surface brush. Applies to the toast only; frames
        /// never get a background.
        /// </summary>
        public string ToastBackgroundImagePath
        {
            get => _toastBackgroundImagePath;
            set => SetValue(ref _toastBackgroundImagePath, value);
        }

        public NotificationStyleSettings Clone()
        {
            return new NotificationStyleSettings
            {
                Toast = Toast.Clone(),
                Frame = Frame.Clone(),
                ToastBackgroundImagePath = ToastBackgroundImagePath
            };
        }

        public static NotificationStyleSettings CreateDefault()
        {
            return new NotificationStyleSettings();
        }
    }

    /// <summary>
    /// Per-surface (toast or frame) appearance style: field visibility, text line order,
    /// fonts, the provider icon toggle, and the surface's own badge images and header texts.
    /// Null line order and null font values mean the built-in defaults (theme-derived fonts,
    /// default line order).
    /// </summary>
    public sealed class NotificationSurfaceStyle : ObservableObject
    {
        public const string LineHeader = "Header";
        public const string LineTitle = "Title";
        public const string LineDescription = "Description";
        public const string LineGameCategory = "GameCategory";

        /// <summary>
        /// The built-in text line order, top to bottom.
        /// </summary>
        public static IReadOnlyList<string> DefaultLineOrder { get; } =
            new[] { LineHeader, LineTitle, LineDescription, LineGameCategory };

        private bool _showHeader = true;
        private bool _showName = true;
        private bool _showDescription = true;
        private bool _showCategory = true;
        private bool _showGameName = true;
        private bool _showRarityBadge = true;
        private bool _showRarityPercent = true;
        private bool _inlineRarityBadge;
        private bool _rightRarityBadge;
        private bool _rarityPercentUnderBadge;
        private bool _showRarityGlow = true;
        private bool _notificationBorderGlow;
        private bool _rarityColoredName = true;
        private bool _showUnlockTime;
        private bool _showProviderIcon = true;
        private bool _showAccentStrip = true;
        private bool _showCountdownBar = true;
        private string _countdownBarColor;
        private FrameVignetteStyle _frameVignette = FrameVignetteStyle.Full;
        private List<string> _lineOrder;
        private string _fontFamily;
        private string _headerFontFamily;
        private string _titleFontFamily;
        private string _bodyFontFamily;
        private string _gameCategoryFontFamily;
        private string _rarityFontFamily;
        private double? _headerFontSize;
        private double? _titleFontSize;
        private double? _bodyFontSize;
        private double? _gameCategoryFontSize;
        private double? _rarityFontSize;
        private NotificationLineEmphasis _headerEmphasis;
        private NotificationLineEmphasis _titleEmphasis;
        private NotificationLineEmphasis _bodyEmphasis;
        private NotificationLineEmphasis _gameCategoryEmphasis;
        private NotificationLineEmphasis _rarityEmphasis;
        private double? _cardWidth;
        private double? _cardHeight;
        private double? _iconSize;
        private double? _rarityBadgeSize;
        private double? _providerIconSize;
        private double? _cardPaddingLeft;
        private double? _cardPaddingRight;
        private double? _linePadding;
        private double _titleLineOffset;
        private double? _textShadowOpacity;
        private double? _textShadowOffset;
        private double? _imageShadowOpacity;
        private double? _imageShadowOffset;
        private NotificationBadgeImageSet _badgeImages;
        private NotificationHeaderTextSettings _headerTexts;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
        public bool ShowHeader
        {
            get => _showHeader;
            set => SetValue(ref _showHeader, value);
        }

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
        public bool ShowName
        {
            get => _showName;
            set => SetValue(ref _showName, value);
        }

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
        public bool ShowDescription
        {
            get => _showDescription;
            set => SetValue(ref _showDescription, value);
        }

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
        public bool ShowCategory
        {
            get => _showCategory;
            set => SetValue(ref _showCategory, value);
        }

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
        public bool ShowGameName
        {
            get => _showGameName;
            set => SetValue(ref _showGameName, value);
        }

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
        public bool ShowRarityBadge
        {
            get => _showRarityBadge;
            set => SetValue(ref _showRarityBadge, value);
        }

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
        public bool ShowRarityPercent
        {
            get => _showRarityPercent;
            set => SetValue(ref _showRarityPercent, value);
        }

        /// <summary>
        /// When true, the rarity/trophy badge is drawn inline before the achievement name
        /// instead of in the icon-column footer. Mutually exclusive with the footer badge in
        /// the settings UI, though independent in the model.
        /// </summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
        public bool InlineRarityBadge
        {
            get => _inlineRarityBadge;
            set => SetValue(ref _inlineRarityBadge, value);
        }

        /// <summary>
        /// When true, the rarity/trophy badge is drawn larger on the right side of the surface,
        /// replacing the provider icon (the provider icon is hidden while this is on).
        /// Mutually exclusive with the footer and inline badge placements in the settings UI.
        /// </summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
        public bool RightRarityBadge
        {
            get => _rightRarityBadge;
            set => SetValue(ref _rightRarityBadge, value);
        }

        /// <summary>
        /// When true, the rarity percent is placed with the badge rather than in the icon-column
        /// footer. It renders under the right-side badge when <see cref="RightRarityBadge"/> is on;
        /// otherwise it stays in the footer alongside the footer badge.
        /// </summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
        public bool RarityPercentUnderBadge
        {
            get => _rarityPercentUnderBadge;
            set => SetValue(ref _rarityPercentUnderBadge, value);
        }

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
        public bool ShowRarityGlow
        {
            get => _showRarityGlow;
            set => SetValue(ref _showRarityGlow, value);
        }

        /// <summary>
        /// When true, a rarity-colored glow is drawn on the notification card border. Toast
        /// surface only; the frame has no card border and ignores this.
        /// </summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
        public bool NotificationBorderGlow
        {
            get => _notificationBorderGlow;
            set => SetValue(ref _notificationBorderGlow, value);
        }

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
        public bool RarityColoredName
        {
            get => _rarityColoredName;
            set => SetValue(ref _rarityColoredName, value);
        }

        /// <summary>
        /// Shows the unlock datetime on the surface's header line. Defaults differ per
        /// surface: off for the toast, on for the frame.
        /// </summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
        public bool ShowUnlockTime
        {
            get => _showUnlockTime;
            set => SetValue(ref _showUnlockTime, value);
        }

        /// <summary>
        /// Shows the unlock's provider icon on the right side of the surface.
        /// </summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
        public bool ShowProviderIcon
        {
            get => _showProviderIcon;
            set => SetValue(ref _showProviderIcon, value);
        }

        /// <summary>
        /// Shows the left-edge rarity accent strip. Toast surface only; the frame has no accent
        /// strip and ignores this.
        /// </summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
        public bool ShowAccentStrip
        {
            get => _showAccentStrip;
            set => SetValue(ref _showAccentStrip, value);
        }

        /// <summary>
        /// Shows the bottom countdown/auto-dismiss timer bar. Toast surface only; the frame is a
        /// static image and ignores this. Hiding the bar does not change the dismiss timing.
        /// </summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
        public bool ShowCountdownBar
        {
            get => _showCountdownBar;
            set => SetValue(ref _showCountdownBar, value);
        }

        /// <summary>
        /// Custom color (e.g. "#RRGGBB") for the countdown timer bar, or null/blank to follow
        /// the default progress-fill brush. Toast surface only.
        /// </summary>
        public string CountdownBarColor
        {
            get => _countdownBarColor;
            set => SetValue(ref _countdownBarColor, value);
        }

        /// <summary>
        /// Vignette darkening on the screenshot frame. Frame surface only; the toast has its own
        /// card chrome and ignores this. Defaults to <see cref="FrameVignetteStyle.Full"/> to
        /// preserve the original radial-plus-bottom-wash look.
        /// </summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
        public FrameVignetteStyle FrameVignette
        {
            get => _frameVignette;
            set => SetValue(ref _frameVignette, value);
        }

        /// <summary>
        /// Toast card width in DIPs, or null for the default (410). Toast surface only; the
        /// frame is composited at the screenshot's size and ignores this.
        /// </summary>
        public double? CardWidth
        {
            get => _cardWidth;
            set => SetValue(ref _cardWidth, value);
        }

        /// <summary>
        /// Toast card height in DIPs, or null to size to content (the natural height, floored by
        /// the template's MinHeight). When set, it is a fixed height — the card renders at exactly
        /// this height and clamped text fits within it, so a background image keeps its aspect
        /// ratio. Toast surface only.
        /// </summary>
        public double? CardHeight
        {
            get => _cardHeight;
            set => SetValue(ref _cardHeight, value);
        }

        /// <summary>
        /// Achievement icon render size in DIPs, or null for the surface default (55 toast / 84
        /// frame).
        /// </summary>
        public double? IconSize
        {
            get => _iconSize;
            set => SetValue(ref _iconSize, value);
        }

        /// <summary>
        /// Rarity/trophy badge render size in DIPs, applied to every badge placement (footer,
        /// inline, and the large right-side badge), or null for the per-placement defaults.
        /// </summary>
        public double? RarityBadgeSize
        {
            get => _rarityBadgeSize;
            set => SetValue(ref _rarityBadgeSize, value);
        }

        /// <summary>
        /// Provider (platform) icon render size in DIPs, or null for the surface default (24 toast
        /// / 40 frame).
        /// </summary>
        public double? ProviderIconSize
        {
            get => _providerIconSize;
            set => SetValue(ref _providerIconSize, value);
        }

        /// <summary>
        /// Left padding in DIPs for the card content, or null for none. Toast surface only; keeps
        /// a background image full-bleed while insetting the content.
        /// </summary>
        public double? CardPaddingLeft
        {
            get => _cardPaddingLeft;
            set => SetValue(ref _cardPaddingLeft, value);
        }

        /// <summary>
        /// Right padding in DIPs for the card content, or null for none. Toast surface only.
        /// </summary>
        public double? CardPaddingRight
        {
            get => _cardPaddingRight;
            set => SetValue(ref _cardPaddingRight, value);
        }

        /// <summary>
        /// Extra top and bottom padding in DIPs added around each text line, or null for none.
        /// </summary>
        public double? LinePadding
        {
            get => _linePadding;
            set => SetValue(ref _linePadding, value);
        }

        /// <summary>
        /// Horizontal offset in DIPs for the achievement-name (title) line, including its inline
        /// badge, so the user can slide the whole line to align it with the rows below. Zero (the
        /// default) leaves the line at its natural start.
        /// </summary>
        public double TitleLineOffset
        {
            get => _titleLineOffset;
            set => SetValue(ref _titleLineOffset, value);
        }

        /// <summary>
        /// User-defined text line order using the Line* tokens, or null for
        /// <see cref="DefaultLineOrder"/>. Kept null until the user reorders so the default
        /// can evolve without stale persisted copies.
        /// </summary>
        public List<string> LineOrder
        {
            get => _lineOrder;
            set => SetValue(ref _lineOrder, value);
        }

        /// <summary>
        /// Font family name for all surface text, or null/blank for the theme-derived family.
        /// Individual lines may override it via the per-line *FontFamily properties.
        /// </summary>
        public string FontFamily
        {
            get => _fontFamily;
            set => SetValue(ref _fontFamily, value);
        }

        /// <summary>
        /// Font family override for the header line, or null/blank to follow
        /// <see cref="FontFamily"/>.
        /// </summary>
        public string HeaderFontFamily
        {
            get => _headerFontFamily;
            set => SetValue(ref _headerFontFamily, value);
        }

        /// <summary>
        /// Font family override for the achievement title line, or null/blank to follow
        /// <see cref="FontFamily"/>.
        /// </summary>
        public string TitleFontFamily
        {
            get => _titleFontFamily;
            set => SetValue(ref _titleFontFamily, value);
        }

        /// <summary>
        /// Font family override for the description line, or null/blank to follow
        /// <see cref="FontFamily"/>.
        /// </summary>
        public string BodyFontFamily
        {
            get => _bodyFontFamily;
            set => SetValue(ref _bodyFontFamily, value);
        }

        /// <summary>
        /// Font family override for the game/category line, or null/blank to follow
        /// <see cref="FontFamily"/>.
        /// </summary>
        public string GameCategoryFontFamily
        {
            get => _gameCategoryFontFamily;
            set => SetValue(ref _gameCategoryFontFamily, value);
        }

        /// <summary>
        /// Font family override for the rarity percent text, or null/blank to follow
        /// <see cref="FontFamily"/>.
        /// </summary>
        public string RarityFontFamily
        {
            get => _rarityFontFamily;
            set => SetValue(ref _rarityFontFamily, value);
        }

        /// <summary>
        /// Font size for the header/caption line (the header row), or null for the theme-derived
        /// size. The rarity percent text has its own <see cref="RarityFontSize"/>.
        /// </summary>
        public double? HeaderFontSize
        {
            get => _headerFontSize;
            set => SetValue(ref _headerFontSize, value);
        }

        /// <summary>
        /// Font size for the achievement title line, or null for the theme-derived size.
        /// </summary>
        public double? TitleFontSize
        {
            get => _titleFontSize;
            set => SetValue(ref _titleFontSize, value);
        }

        /// <summary>
        /// Font size for the description line, or null for the theme-derived size.
        /// </summary>
        public double? BodyFontSize
        {
            get => _bodyFontSize;
            set => SetValue(ref _bodyFontSize, value);
        }

        /// <summary>
        /// Font size for the game/category line, or null for the theme-derived size. Independent
        /// of the header line size.
        /// </summary>
        public double? GameCategoryFontSize
        {
            get => _gameCategoryFontSize;
            set => SetValue(ref _gameCategoryFontSize, value);
        }

        /// <summary>
        /// Font size for the rarity percent text (footer, inline, and right-side badge
        /// placements), or null for the theme-derived caption size. Independent of the header
        /// line size so the percent can be sized on its own.
        /// </summary>
        public double? RarityFontSize
        {
            get => _rarityFontSize;
            set => SetValue(ref _rarityFontSize, value);
        }

        /// <summary>
        /// Whole-line emphasis (bold/italic/underline/strikethrough) for the header line.
        /// </summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
        public NotificationLineEmphasis HeaderEmphasis
        {
            get => _headerEmphasis;
            set => SetValue(ref _headerEmphasis, value);
        }

        /// <summary>
        /// Whole-line emphasis for the achievement title line. Bold raises the line's built-in
        /// SemiBold weight to Bold.
        /// </summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
        public NotificationLineEmphasis TitleEmphasis
        {
            get => _titleEmphasis;
            set => SetValue(ref _titleEmphasis, value);
        }

        /// <summary>
        /// Whole-line emphasis for the description line.
        /// </summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
        public NotificationLineEmphasis BodyEmphasis
        {
            get => _bodyEmphasis;
            set => SetValue(ref _bodyEmphasis, value);
        }

        /// <summary>
        /// Whole-line emphasis for the game/category line.
        /// </summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
        public NotificationLineEmphasis GameCategoryEmphasis
        {
            get => _gameCategoryEmphasis;
            set => SetValue(ref _gameCategoryEmphasis, value);
        }

        /// <summary>
        /// Whole-line emphasis for the rarity percent text.
        /// </summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
        public NotificationLineEmphasis RarityEmphasis
        {
            get => _rarityEmphasis;
            set => SetValue(ref _rarityEmphasis, value);
        }

        /// <summary>
        /// Darkness of the drop shadow behind the surface's text, badges, and logos, as a
        /// percentage: 50 matches the built-in shadow, higher values stack a second layer up
        /// to solid black, 0 disables it. Null means the built-in default (50).
        /// </summary>
        public double? TextShadowOpacity
        {
            get => _textShadowOpacity;
            set => SetValue(ref _textShadowOpacity, value);
        }

        /// <summary>
        /// How far the drop shadow sits from the glyphs, as a percentage of the maximum
        /// offset: 25 matches the built-in shadow, 0 puts it directly behind the text. Null
        /// means the built-in default (25).
        /// </summary>
        public double? TextShadowOffset
        {
            get => _textShadowOffset;
            set => SetValue(ref _textShadowOffset, value);
        }

        /// <summary>
        /// Darkness of the drop shadow behind the surface's images (badge images and the
        /// provider icon), as a percentage: 100 matches the built-in shadow (images use a
        /// single layer, so 100 is the darkest), 0 disables it. Null means the built-in
        /// default (100).
        /// </summary>
        public double? ImageShadowOpacity
        {
            get => _imageShadowOpacity;
            set => SetValue(ref _imageShadowOpacity, value);
        }

        /// <summary>
        /// How far the image drop shadow sits from the artwork, as a percentage of the
        /// maximum offset: 25 matches the built-in shadow. Null means the built-in default
        /// (25).
        /// </summary>
        public double? ImageShadowOffset
        {
            get => _imageShadowOffset;
            set => SetValue(ref _imageShadowOffset, value);
        }

        /// <summary>
        /// User-supplied badge replacement images for this surface. Lazily initialized;
        /// never null.
        /// </summary>
        public NotificationBadgeImageSet BadgeImages
        {
            get => _badgeImages ?? (_badgeImages = new NotificationBadgeImageSet());
            set => SetValue(ref _badgeImages, value);
        }

        /// <summary>
        /// User-edited header strings for this surface. Lazily initialized; never null.
        /// </summary>
        public NotificationHeaderTextSettings HeaderTexts
        {
            get => _headerTexts ?? (_headerTexts = new NotificationHeaderTextSettings());
            set => SetValue(ref _headerTexts, value);
        }

        /// <summary>
        /// Returns a complete line order: known tokens from <paramref name="order"/> in their
        /// stored order (case-insensitive, deduplicated), with any missing default lines
        /// appended. Null or empty input yields <see cref="DefaultLineOrder"/>.
        /// </summary>
        public static List<string> CanonicalizeLineOrder(IEnumerable<string> order)
        {
            var result = new List<string>(DefaultLineOrder.Count);
            foreach (var token in order ?? Enumerable.Empty<string>())
            {
                var known = DefaultLineOrder.FirstOrDefault(line =>
                    string.Equals(line, token?.Trim(), StringComparison.OrdinalIgnoreCase));
                if (known != null && !result.Contains(known))
                {
                    result.Add(known);
                }
            }

            foreach (var line in DefaultLineOrder)
            {
                if (!result.Contains(line))
                {
                    result.Add(line);
                }
            }

            return result;
        }

        public NotificationSurfaceStyle Clone()
        {
            return new NotificationSurfaceStyle
            {
                ShowHeader = ShowHeader,
                ShowName = ShowName,
                ShowDescription = ShowDescription,
                ShowCategory = ShowCategory,
                ShowGameName = ShowGameName,
                ShowRarityBadge = ShowRarityBadge,
                ShowRarityPercent = ShowRarityPercent,
                InlineRarityBadge = InlineRarityBadge,
                RightRarityBadge = RightRarityBadge,
                RarityPercentUnderBadge = RarityPercentUnderBadge,
                ShowRarityGlow = ShowRarityGlow,
                NotificationBorderGlow = NotificationBorderGlow,
                RarityColoredName = RarityColoredName,
                ShowUnlockTime = ShowUnlockTime,
                ShowProviderIcon = ShowProviderIcon,
                ShowAccentStrip = ShowAccentStrip,
                ShowCountdownBar = ShowCountdownBar,
                CountdownBarColor = CountdownBarColor,
                FrameVignette = FrameVignette,
                LineOrder = LineOrder != null ? new List<string>(LineOrder) : null,
                FontFamily = FontFamily,
                HeaderFontFamily = HeaderFontFamily,
                TitleFontFamily = TitleFontFamily,
                BodyFontFamily = BodyFontFamily,
                GameCategoryFontFamily = GameCategoryFontFamily,
                RarityFontFamily = RarityFontFamily,
                HeaderFontSize = HeaderFontSize,
                TitleFontSize = TitleFontSize,
                BodyFontSize = BodyFontSize,
                GameCategoryFontSize = GameCategoryFontSize,
                RarityFontSize = RarityFontSize,
                HeaderEmphasis = HeaderEmphasis,
                TitleEmphasis = TitleEmphasis,
                BodyEmphasis = BodyEmphasis,
                GameCategoryEmphasis = GameCategoryEmphasis,
                RarityEmphasis = RarityEmphasis,
                CardWidth = CardWidth,
                CardHeight = CardHeight,
                IconSize = IconSize,
                RarityBadgeSize = RarityBadgeSize,
                ProviderIconSize = ProviderIconSize,
                CardPaddingLeft = CardPaddingLeft,
                CardPaddingRight = CardPaddingRight,
                LinePadding = LinePadding,
                TitleLineOffset = TitleLineOffset,
                TextShadowOpacity = TextShadowOpacity,
                TextShadowOffset = TextShadowOffset,
                ImageShadowOpacity = ImageShadowOpacity,
                ImageShadowOffset = ImageShadowOffset,
                BadgeImages = BadgeImages.Clone(),
                HeaderTexts = HeaderTexts.Clone()
            };
        }

        public static NotificationSurfaceStyle CreateToastDefault()
        {
            return new NotificationSurfaceStyle { ShowUnlockTime = false };
        }

        public static NotificationSurfaceStyle CreateFrameDefault()
        {
            return new NotificationSurfaceStyle { ShowUnlockTime = true };
        }
    }

    /// <summary>
    /// User-supplied badge replacement images (absolute paths; null = plugin-drawn badge).
    /// A set rarity image displays instead of the trophy badge for trophy-typed unlocks.
    /// Applies to the toast and frame surfaces only; the rest of the app keeps drawn badges.
    /// </summary>
    public sealed class NotificationBadgeImageSet : ObservableObject
    {
        private string _commonPath;
        private string _uncommonPath;
        private string _rarePath;
        private string _ultraRarePath;
        private string _completionPath;

        public string CommonPath
        {
            get => _commonPath;
            set => SetValue(ref _commonPath, value);
        }

        public string UncommonPath
        {
            get => _uncommonPath;
            set => SetValue(ref _uncommonPath, value);
        }

        public string RarePath
        {
            get => _rarePath;
            set => SetValue(ref _rarePath, value);
        }

        public string UltraRarePath
        {
            get => _ultraRarePath;
            set => SetValue(ref _ultraRarePath, value);
        }

        public string CompletionPath
        {
            get => _completionPath;
            set => SetValue(ref _completionPath, value);
        }

        public NotificationBadgeImageSet Clone()
        {
            return new NotificationBadgeImageSet
            {
                CommonPath = CommonPath,
                UncommonPath = UncommonPath,
                RarePath = RarePath,
                UltraRarePath = UltraRarePath,
                CompletionPath = CompletionPath
            };
        }
    }

    /// <summary>
    /// User-edited notification header strings. Null or blank values follow the current
    /// localized default; unedited stored values are re-normalized to null at startup by
    /// NotificationHeaderTextService so they keep following language changes. The friend
    /// variants are format strings where {0} is the friend's display name.
    /// </summary>
    public sealed class NotificationHeaderTextSettings : ObservableObject
    {
        private string _unlockHeader;
        private string _friendUnlockHeaderFormat;
        private string _completionHeader;
        private string _friendCompletionHeaderFormat;

        public string UnlockHeader
        {
            get => _unlockHeader;
            set => SetValue(ref _unlockHeader, value);
        }

        public string FriendUnlockHeaderFormat
        {
            get => _friendUnlockHeaderFormat;
            set => SetValue(ref _friendUnlockHeaderFormat, value);
        }

        public string CompletionHeader
        {
            get => _completionHeader;
            set => SetValue(ref _completionHeader, value);
        }

        public string FriendCompletionHeaderFormat
        {
            get => _friendCompletionHeaderFormat;
            set => SetValue(ref _friendCompletionHeaderFormat, value);
        }

        public NotificationHeaderTextSettings Clone()
        {
            return new NotificationHeaderTextSettings
            {
                UnlockHeader = UnlockHeader,
                FriendUnlockHeaderFormat = FriendUnlockHeaderFormat,
                CompletionHeader = CompletionHeader,
                FriendCompletionHeaderFormat = FriendCompletionHeaderFormat
            };
        }
    }
}
