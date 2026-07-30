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
        private NotificationBadgeImageSet _badgeImages;
        private NotificationHeaderTextSettings _headerTexts;

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
        /// Absolute path of a user-supplied toast background image (png/jpg/gif), or null for
        /// the default surface brush. Applies to the toast only; frames never get a background.
        /// </summary>
        public string ToastBackgroundImagePath
        {
            get => _toastBackgroundImagePath;
            set => SetValue(ref _toastBackgroundImagePath, value);
        }

        /// <summary>
        /// User-supplied badge replacement images shared by both surfaces. Lazily initialized;
        /// never null.
        /// </summary>
        public NotificationBadgeImageSet BadgeImages
        {
            get => _badgeImages ?? (_badgeImages = new NotificationBadgeImageSet());
            set => SetValue(ref _badgeImages, value);
        }

        /// <summary>
        /// User-edited header strings shared by both surfaces. Lazily initialized; never null.
        /// </summary>
        public NotificationHeaderTextSettings HeaderTexts
        {
            get => _headerTexts ?? (_headerTexts = new NotificationHeaderTextSettings());
            set => SetValue(ref _headerTexts, value);
        }

        public NotificationStyleSettings Clone()
        {
            return new NotificationStyleSettings
            {
                Toast = Toast.Clone(),
                Frame = Frame.Clone(),
                ToastBackgroundImagePath = ToastBackgroundImagePath,
                BadgeImages = BadgeImages.Clone(),
                HeaderTexts = HeaderTexts.Clone()
            };
        }

        public static NotificationStyleSettings CreateDefault()
        {
            return new NotificationStyleSettings();
        }
    }

    /// <summary>
    /// Per-surface (toast or frame) appearance style: field visibility, text line order,
    /// fonts, and the provider icon toggle. Null line order and null font values mean the
    /// built-in defaults (theme-derived fonts, default line order).
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
        private List<string> _lineOrder;
        private string _fontFamily;
        private double? _headerFontSize;
        private double? _titleFontSize;
        private double? _bodyFontSize;
        private double? _cardWidth;
        private double? _cardHeight;
        private double? _iconSize;
        private double? _rarityBadgeSize;
        private double? _providerIconSize;
        private double _titleLineOffset;

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
        /// </summary>
        public string FontFamily
        {
            get => _fontFamily;
            set => SetValue(ref _fontFamily, value);
        }

        /// <summary>
        /// Font size for the header/caption lines (header row, game and category row, percent
        /// text), or null for the theme-derived size.
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
                LineOrder = LineOrder != null ? new List<string>(LineOrder) : null,
                FontFamily = FontFamily,
                HeaderFontSize = HeaderFontSize,
                TitleFontSize = TitleFontSize,
                BodyFontSize = BodyFontSize,
                CardWidth = CardWidth,
                CardHeight = CardHeight,
                IconSize = IconSize,
                RarityBadgeSize = RarityBadgeSize,
                ProviderIconSize = ProviderIconSize,
                TitleLineOffset = TitleLineOffset
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
