using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services.UI;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.ViewModels.Settings
{
    /// <summary>
    /// Backs one surface (toast or screenshot frame) of the notification appearance editor.
    /// Wraps the currently selected <see cref="NotificationStyleSettings"/> (the global
    /// default or a provider's whole-style copy) and observes its INPC objects: any edit
    /// schedules a debounced persist and raises <see cref="StyleChanged"/> so the host section
    /// can refresh its live mockups. Header texts are the exception: they commit only through
    /// <see cref="ApplyHeaderTexts"/> so format strings never persist per keystroke.
    /// </summary>
    internal sealed class NotificationAppearanceEditorViewModel : ObservableObject, IDisposable
    {
        private static IReadOnlyList<FontFamilyOption> _fontFamilyOptions;

        private readonly PlayniteAchievementsSettings _settings;
        private readonly PlayniteAchievementsPlugin _plugin;
        private readonly ILogger _logger;
        private readonly DispatcherTimer _persistDebounceTimer;
        private bool _hasPendingPersist;

        private NotificationStyleSettings _style;
        private NotificationSurfaceStyle _subscribedSurface;
        private NotificationBadgeImageSet _subscribedBadges;
        private NotificationHeaderTextSettings _subscribedHeaderTexts;
        private string _providerKey;
        private NotificationImageOwner _imageOwner = NotificationImageOwner.Global;
        private Action<NotificationStyleSettings> _persistStyle;
        private bool _isEditable = true;

        private string _unlockHeaderText;
        private string _friendUnlockHeaderText;
        private string _completionHeaderText;
        private string _friendCompletionHeaderText;
        private bool _hasHeaderFormatError;
        private string _textShadowText;
        private string _cardWidthText = string.Empty;
        private string _cardHeightText = string.Empty;
        private string _iconSizeText = string.Empty;
        private string _rarityBadgeSizeText = string.Empty;
        private string _rarityFontSizeText = string.Empty;
        private string _providerIconSizeText = string.Empty;
        private string _cardPaddingLeftText = string.Empty;
        private string _cardPaddingRightText = string.Empty;
        private string _linePaddingText = string.Empty;

        public NotificationAppearanceEditorViewModel(
            PlayniteAchievementsSettings settings,
            PlayniteAchievementsPlugin plugin,
            ILogger logger,
            bool isFrameSurface)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _logger = logger;
            IsFrameSurface = isFrameSurface;

            _persistDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _persistDebounceTimer.Tick += OnPersistDebounceTimerTick;

            LineRows = new ObservableCollection<NotificationLineRowItem>(
                NotificationSurfaceStyle.DefaultLineOrder.Select(kind =>
                    new NotificationLineRowItem(kind, BuildLineDisplayName(kind), OnLineRowSizeEdited)));
        }

        /// <summary>
        /// Raised after any observed style edit (and after header Apply) so the host section
        /// can rebuild its mockups from the edited values.
        /// </summary>
        public event EventHandler StyleChanged;

        public bool IsFrameSurface { get; }

        public bool IsToastSurface => !IsFrameSurface;

        /// <summary>
        /// The style object being edited: the global default or a provider's copy. Null-safe
        /// for bindings while no style is set.
        /// </summary>
        public NotificationStyleSettings Style => _style;

        public NotificationSurfaceStyle Surface =>
            IsFrameSurface ? _style?.Frame : _style?.Toast;

        /// <summary>
        /// The provider key owning the edited copy, or null when editing the global default.
        /// Also selects the notification image slot folder.
        /// </summary>
        public string ProviderKey => _providerKey;

        /// <summary>
        /// False when a platform without a custom style is selected: the editor then shows the
        /// default values read-only until the user customizes the platform.
        /// </summary>
        public bool IsEditable => _isEditable;

        public ObservableCollection<NotificationLineRowItem> LineRows { get; }

        private static readonly object _fontFamilyOptionsGate = new object();

        public IReadOnlyList<FontFamilyOption> FontFamilyOptions => EnsureFontFamilyOptions();

        /// <summary>
        /// Builds (once, process-wide) the system font-family list. Enumerating
        /// <see cref="Fonts.SystemFontFamilies"/> and culture-sorting it is slow, so it is cached and
        /// guarded so a background pre-warm and the first UI access never build it twice.
        /// </summary>
        private static IReadOnlyList<FontFamilyOption> EnsureFontFamilyOptions()
        {
            if (_fontFamilyOptions != null)
            {
                return _fontFamilyOptions;
            }

            lock (_fontFamilyOptionsGate)
            {
                return _fontFamilyOptions ?? (_fontFamilyOptions = BuildFontFamilyOptions());
            }
        }

        /// <summary>
        /// Pre-builds the font-family list off the UI thread so the first open of the notification
        /// appearance tab doesn't block on the enumeration. Safe to call repeatedly; best-effort.
        /// </summary>
        public static void PrewarmFontOptions()
        {
            try
            {
                EnsureFontFamilyOptions();
            }
            catch
            {
                // Falls back to the lazy UI-thread build on first access.
            }
        }

        public FontFamilyOption SelectedFontFamilyOption
        {
            get
            {
                var familyName = Surface?.FontFamily;
                if (string.IsNullOrWhiteSpace(familyName))
                {
                    return FontFamilyOptions.FirstOrDefault();
                }

                return FontFamilyOptions.FirstOrDefault(option =>
                           string.Equals(option.FamilyName, familyName, StringComparison.OrdinalIgnoreCase))
                       ?? FontFamilyOptions.FirstOrDefault();
            }
            set
            {
                var surface = Surface;
                if (surface == null)
                {
                    return;
                }

                var familyName = value?.FamilyName;
                if (!string.Equals(surface.FontFamily, familyName, StringComparison.Ordinal))
                {
                    surface.FontFamily = familyName;
                }
            }
        }

        #region Rarity badge / percent placement (two dropdowns over the surface flags)

        private static IReadOnlyList<RarityBadgePlacementOption> _badgePlacementOptions;
        private static IReadOnlyList<RarityPercentPlacementOption> _percentPlacementOptions;

        public IReadOnlyList<RarityBadgePlacementOption> BadgePlacementOptions =>
            _badgePlacementOptions ?? (_badgePlacementOptions = new[]
            {
                new RarityBadgePlacementOption(RarityBadgePlacement.None, L("LOCPlayAch_Common_None")),
                new RarityBadgePlacementOption(RarityBadgePlacement.UnderIcon, L("LOCPlayAch_Settings_Style_Rarity_BadgeUnderIcon")),
                new RarityBadgePlacementOption(RarityBadgePlacement.Inline, L("LOCPlayAch_Settings_Style_Rarity_InlineBadge")),
                new RarityBadgePlacementOption(RarityBadgePlacement.Right, L("LOCPlayAch_Settings_Style_Rarity_BadgeRight"))
            });

        public IReadOnlyList<RarityPercentPlacementOption> PercentPlacementOptions =>
            _percentPlacementOptions ?? (_percentPlacementOptions = new[]
            {
                new RarityPercentPlacementOption(RarityPercentPlacement.None, L("LOCPlayAch_Common_None")),
                new RarityPercentPlacementOption(RarityPercentPlacement.UnderIcon, L("LOCPlayAch_Settings_Style_Rarity_PercentUnderIcon")),
                new RarityPercentPlacementOption(RarityPercentPlacement.WithBadge, L("LOCPlayAch_Settings_Style_Rarity_PercentWithBadge"))
            });

        /// <summary>
        /// Rarity badge placement, derived from and written back to the surface's footer / inline /
        /// right badge flags, which the dropdown keeps mutually exclusive.
        /// </summary>
        public RarityBadgePlacementOption SelectedBadgePlacement
        {
            get
            {
                var surface = Surface;
                RarityBadgePlacement value;
                if (surface == null)
                {
                    value = RarityBadgePlacement.UnderIcon;
                }
                else if (surface.RightRarityBadge)
                {
                    value = RarityBadgePlacement.Right;
                }
                else if (surface.InlineRarityBadge)
                {
                    value = RarityBadgePlacement.Inline;
                }
                else if (surface.ShowRarityBadge)
                {
                    value = RarityBadgePlacement.UnderIcon;
                }
                else
                {
                    value = RarityBadgePlacement.None;
                }

                return BadgePlacementOptions.FirstOrDefault(option => option.Value == value)
                       ?? BadgePlacementOptions[0];
            }
            set
            {
                var surface = Surface;
                if (surface == null || value == null)
                {
                    return;
                }

                var mode = value.Value;
                surface.ShowRarityBadge = mode == RarityBadgePlacement.UnderIcon;
                surface.InlineRarityBadge = mode == RarityBadgePlacement.Inline;
                surface.RightRarityBadge = mode == RarityBadgePlacement.Right;
            }
        }

        /// <summary>
        /// Rarity percent placement, derived from and written back to the surface's percent
        /// visibility and under-badge flags.
        /// </summary>
        public RarityPercentPlacementOption SelectedPercentPlacement
        {
            get
            {
                var surface = Surface;
                RarityPercentPlacement value;
                if (surface == null)
                {
                    value = RarityPercentPlacement.UnderIcon;
                }
                else if (!surface.ShowRarityPercent)
                {
                    value = RarityPercentPlacement.None;
                }
                else if (surface.RarityPercentUnderBadge)
                {
                    value = RarityPercentPlacement.WithBadge;
                }
                else
                {
                    value = RarityPercentPlacement.UnderIcon;
                }

                return PercentPlacementOptions.FirstOrDefault(option => option.Value == value)
                       ?? PercentPlacementOptions[0];
            }
            set
            {
                var surface = Surface;
                if (surface == null || value == null)
                {
                    return;
                }

                var mode = value.Value;
                surface.ShowRarityPercent = mode != RarityPercentPlacement.None;
                surface.RarityPercentUnderBadge = mode == RarityPercentPlacement.WithBadge;
            }
        }

        /// <summary>
        /// Whether the provider-icon toggle is meaningful: the right-side badge replaces the
        /// provider icon, so the toggle is disabled while that badge placement is selected.
        /// </summary>
        public bool IsProviderIconEnabled => Surface != null && !Surface.RightRarityBadge;

        #endregion

        #region Glow display (single dropdown over icon glow + border glow flags)

        private static IReadOnlyList<GlowDisplayOption> _toastGlowDisplayOptions;
        private static IReadOnlyList<GlowDisplayOption> _frameGlowDisplayOptions;

        /// <summary>
        /// Glow choices for the surface. The frame has no card border, so it only offers the
        /// icon glow (Icon/None); the toast additionally offers the border glow (Notification)
        /// and Both.
        /// </summary>
        public IReadOnlyList<GlowDisplayOption> GlowDisplayOptions => IsFrameSurface
            ? (_frameGlowDisplayOptions ?? (_frameGlowDisplayOptions = new[]
            {
                new GlowDisplayOption(GlowDisplay.Icon, L("LOCPlayAch_Column_Icon")),
                new GlowDisplayOption(GlowDisplay.None, L("LOCPlayAch_Common_None"))
            }))
            : (_toastGlowDisplayOptions ?? (_toastGlowDisplayOptions = new[]
            {
                new GlowDisplayOption(GlowDisplay.Icon, L("LOCPlayAch_Column_Icon")),
                new GlowDisplayOption(GlowDisplay.Notification, L("LOCPlayAch_Settings_Style_ToastTab")),
                new GlowDisplayOption(GlowDisplay.Both, L("LOCPlayAch_Common_Both")),
                new GlowDisplayOption(GlowDisplay.None, L("LOCPlayAch_Common_None"))
            }));

        /// <summary>
        /// The glow layout, derived from and written back to the surface's icon-glow and
        /// border-glow flags.
        /// </summary>
        public GlowDisplayOption SelectedGlowDisplay
        {
            get
            {
                var surface = Surface;
                GlowDisplay value;
                if (surface == null)
                {
                    value = GlowDisplay.Icon;
                }
                else if (IsFrameSurface)
                {
                    // The frame has no border glow; only the icon glow applies.
                    value = surface.ShowRarityGlow ? GlowDisplay.Icon : GlowDisplay.None;
                }
                else if (surface.ShowRarityGlow && surface.NotificationBorderGlow)
                {
                    value = GlowDisplay.Both;
                }
                else if (surface.NotificationBorderGlow)
                {
                    value = GlowDisplay.Notification;
                }
                else if (surface.ShowRarityGlow)
                {
                    value = GlowDisplay.Icon;
                }
                else
                {
                    value = GlowDisplay.None;
                }

                return GlowDisplayOptions.FirstOrDefault(option => option.Value == value)
                       ?? GlowDisplayOptions[0];
            }
            set
            {
                var surface = Surface;
                if (surface == null || value == null)
                {
                    return;
                }

                var mode = value.Value;
                surface.ShowRarityGlow = mode == GlowDisplay.Icon || mode == GlowDisplay.Both;
                // The frame never has a border glow regardless of the selection.
                surface.NotificationBorderGlow = !IsFrameSurface &&
                    (mode == GlowDisplay.Notification || mode == GlowDisplay.Both);
            }
        }

        #endregion

        #region Frame vignette (frame surface only)

        private static IReadOnlyList<FrameVignetteOption> _frameVignetteOptions;

        /// <summary>
        /// Vignette choices for the screenshot frame: Full (radial edge vignette plus the bottom
        /// contrast wash), Bottom (bottom wash only), or None. Frame surface only; the toast has
        /// its own card chrome, so the dropdown is hidden there.
        /// </summary>
        public IReadOnlyList<FrameVignetteOption> FrameVignetteOptions =>
            _frameVignetteOptions ?? (_frameVignetteOptions = new[]
            {
                new FrameVignetteOption(FrameVignetteStyle.Full, L("LOCPlayAch_Settings_Style_VignetteFull")),
                new FrameVignetteOption(FrameVignetteStyle.Bottom, L("LOCPlayAch_Settings_Style_VignetteBottom")),
                new FrameVignetteOption(FrameVignetteStyle.None, L("LOCPlayAch_Common_None"))
            });

        /// <summary>
        /// The frame vignette style, read from and written back to the surface.
        /// </summary>
        public FrameVignetteOption SelectedFrameVignette
        {
            get
            {
                var value = Surface?.FrameVignette ?? FrameVignetteStyle.Full;
                return FrameVignetteOptions.FirstOrDefault(option => option.Value == value)
                       ?? FrameVignetteOptions[0];
            }
            set
            {
                var surface = Surface;
                if (surface == null || value == null)
                {
                    return;
                }

                surface.FrameVignette = value.Value;
            }
        }

        #endregion

        #region Card dimensions (toast surface only; blank = template default)

        /// <summary>
        /// Toast card width text (LostFocus commit). Blank or invalid clears the override
        /// (falls back to the default width); a positive number stores it.
        /// </summary>
        public string CardWidthText
        {
            get => _cardWidthText;
            set
            {
                if (SetValueAndReturn(ref _cardWidthText, value))
                {
                    CommitCardDimension(value, isWidth: true);
                }
            }
        }

        /// <summary>
        /// Toast card height text (LostFocus commit). Blank or invalid clears the override
        /// (falls back to the default height); a positive number sets a fixed card height.
        /// </summary>
        public string CardHeightText
        {
            get => _cardHeightText;
            set
            {
                if (SetValueAndReturn(ref _cardHeightText, value))
                {
                    CommitCardDimension(value, isWidth: false);
                }
            }
        }

        /// <summary>
        /// Icon size (toast/frame). Blank or invalid clears the override (falls back to the
        /// surface default); a positive number sets it.
        /// </summary>
        public string IconSizeText
        {
            get => _iconSizeText;
            set
            {
                if (SetValueAndReturn(ref _iconSizeText, value))
                {
                    CommitSize(value, (surface, parsed) => surface.IconSize = parsed);
                }
            }
        }

        /// <summary>
        /// Rarity badge size (applies to every badge placement). Blank/invalid clears the override.
        /// </summary>
        public string RarityBadgeSizeText
        {
            get => _rarityBadgeSizeText;
            set
            {
                if (SetValueAndReturn(ref _rarityBadgeSizeText, value))
                {
                    CommitSize(value, (surface, parsed) => surface.RarityBadgeSize = parsed);
                }
            }
        }

        /// <summary>
        /// Rarity percent text size. Blank/invalid clears the override.
        /// </summary>
        public string RarityFontSizeText
        {
            get => _rarityFontSizeText;
            set
            {
                if (SetValueAndReturn(ref _rarityFontSizeText, value))
                {
                    CommitSize(value, (surface, parsed) => surface.RarityFontSize = parsed);
                }
            }
        }

        /// <summary>
        /// Provider (platform) icon size. Blank/invalid clears the override.
        /// </summary>
        public string ProviderIconSizeText
        {
            get => _providerIconSizeText;
            set
            {
                if (SetValueAndReturn(ref _providerIconSizeText, value))
                {
                    CommitSize(value, (surface, parsed) => surface.ProviderIconSize = parsed);
                }
            }
        }

        /// <summary>Left padding of the card content. Blank/invalid clears the override.</summary>
        public string CardPaddingLeftText
        {
            get => _cardPaddingLeftText;
            set
            {
                if (SetValueAndReturn(ref _cardPaddingLeftText, value))
                {
                    CommitSize(value, (surface, parsed) => surface.CardPaddingLeft = parsed);
                }
            }
        }

        /// <summary>Right padding of the card content. Blank/invalid clears the override.</summary>
        public string CardPaddingRightText
        {
            get => _cardPaddingRightText;
            set
            {
                if (SetValueAndReturn(ref _cardPaddingRightText, value))
                {
                    CommitSize(value, (surface, parsed) => surface.CardPaddingRight = parsed);
                }
            }
        }

        /// <summary>Extra top/bottom padding around each text line. Blank/invalid clears it.</summary>
        public string LinePaddingText
        {
            get => _linePaddingText;
            set
            {
                if (SetValueAndReturn(ref _linePaddingText, value))
                {
                    CommitSize(value, (surface, parsed) => surface.LinePadding = parsed);
                }
            }
        }

        private void CommitCardDimension(string text, bool isWidth)
        {
            var surface = Surface;
            if (surface == null || !_isEditable)
            {
                RefreshCardDimensions();
                return;
            }

            var parsed = ParseOptionalPositive(text);
            if (isWidth)
            {
                surface.CardWidth = parsed;
            }
            else
            {
                surface.CardHeight = parsed;
            }

            RefreshCardDimensions();
        }

        private void CommitSize(string text, Action<NotificationSurfaceStyle, double?> apply)
        {
            var surface = Surface;
            if (surface != null && _isEditable)
            {
                apply(surface, ParseOptionalPositive(text));
            }

            RefreshCardDimensions();
        }

        // Parses a blank/invalid entry as "no override" (null) and a positive number as the value,
        // accepting both the current and invariant cultures.
        private static double? ParseOptionalPositive(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var current) &&
                current > 0)
            {
                return current;
            }

            if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var invariant) &&
                invariant > 0)
            {
                return invariant;
            }

            return null;
        }

        private void RefreshCardDimensions()
        {
            var surface = Surface;
            SetValue(ref _cardWidthText,
                surface?.CardWidth?.ToString(CultureInfo.CurrentCulture) ?? string.Empty,
                nameof(CardWidthText));
            SetValue(ref _cardHeightText,
                surface?.CardHeight?.ToString(CultureInfo.CurrentCulture) ?? string.Empty,
                nameof(CardHeightText));
            SetValue(ref _iconSizeText,
                surface?.IconSize?.ToString(CultureInfo.CurrentCulture) ?? string.Empty,
                nameof(IconSizeText));
            SetValue(ref _rarityBadgeSizeText,
                surface?.RarityBadgeSize?.ToString(CultureInfo.CurrentCulture) ?? string.Empty,
                nameof(RarityBadgeSizeText));
            SetValue(ref _rarityFontSizeText,
                surface?.RarityFontSize?.ToString(CultureInfo.CurrentCulture) ?? string.Empty,
                nameof(RarityFontSizeText));
            SetValue(ref _providerIconSizeText,
                surface?.ProviderIconSize?.ToString(CultureInfo.CurrentCulture) ?? string.Empty,
                nameof(ProviderIconSizeText));
            SetValue(ref _cardPaddingLeftText,
                surface?.CardPaddingLeft?.ToString(CultureInfo.CurrentCulture) ?? string.Empty,
                nameof(CardPaddingLeftText));
            SetValue(ref _cardPaddingRightText,
                surface?.CardPaddingRight?.ToString(CultureInfo.CurrentCulture) ?? string.Empty,
                nameof(CardPaddingRightText));
            SetValue(ref _linePaddingText,
                surface?.LinePadding?.ToString(CultureInfo.CurrentCulture) ?? string.Empty,
                nameof(LinePaddingText));
            SetValue(ref _textShadowText,
                surface?.TextShadowOpacity?.ToString(CultureInfo.CurrentCulture) ?? string.Empty,
                nameof(TextShadowText));

            // The slider companions are computed straight from the surface; refresh them
            // together with their text mirrors.
            OnPropertyChanged(nameof(IconSizeSlider));
            OnPropertyChanged(nameof(RarityBadgeSizeSlider));
            OnPropertyChanged(nameof(RarityFontSizeSlider));
            OnPropertyChanged(nameof(ProviderIconSizeSlider));
            OnPropertyChanged(nameof(LinePaddingSlider));
            OnPropertyChanged(nameof(CardWidthSlider));
            OnPropertyChanged(nameof(CardHeightSlider));
            OnPropertyChanged(nameof(CardPaddingLeftSlider));
            OnPropertyChanged(nameof(CardPaddingRightSlider));
            OnPropertyChanged(nameof(TextShadowSlider));
        }

        // Slider/textbox range for the name-line offset; must match the Slider bounds in the view.
        private const double TitleLineOffsetLimit = 50;

        /// <summary>
        /// Editable text mirror of the name-line offset, kept in sync with the slider. Parsing
        /// clamps to the slider range and rounds to a whole DIP; invalid input reverts.
        /// </summary>
        public string TitleLineOffsetText
        {
            get => Surface?.TitleLineOffset.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
            set
            {
                var surface = Surface;
                if (surface != null && _isEditable)
                {
                    var text = value?.Trim();
                    if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed) ||
                        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                    {
                        surface.TitleLineOffset = Math.Max(
                            -TitleLineOffsetLimit,
                            Math.Min(TitleLineOffsetLimit, Math.Round(parsed)));
                    }
                }

                // Reflect the committed (possibly clamped or reverted) value back into the box.
                OnPropertyChanged(nameof(TitleLineOffsetText));
            }
        }

        /// <summary>
        /// Text shadow strength (0-100; 25 matches the built-in shadow, 0 disables it). Blank
        /// clears the override back to the default. Zero is meaningful here, so this commits
        /// through its own parser instead of <see cref="CommitSize"/>.
        /// </summary>
        public string TextShadowText
        {
            get => _textShadowText;
            set
            {
                if (!SetValueAndReturn(ref _textShadowText, value))
                {
                    return;
                }

                var surface = Surface;
                if (surface != null && _isEditable)
                {
                    surface.TextShadowOpacity = ParseShadowStrength(value);
                }

                RefreshCardDimensions();
            }
        }

        public double TextShadowSlider
        {
            get => Surface?.TextShadowOpacity ?? AchievementToastViewModel.DefaultTextShadowStrength;
            set => TextShadowText = Math.Round(Math.Max(0, Math.Min(100, value)))
                .ToString(CultureInfo.CurrentCulture);
        }

        private static double? ParseShadowStrength(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var current) ||
                double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out current))
            {
                return Math.Max(0, Math.Min(100, current));
            }

            return null;
        }

        // Slider companions for the size text boxes: each reads the effective value (the
        // user's override, or the default the renderer applies) and writes through its text
        // mirror, so parsing, persistence, and refresh stay on the one shared path.
        public double IconSizeSlider
        {
            get => Surface?.IconSize ?? (IsFrameSurface
                ? AchievementToastViewModel.DefaultFrameIconSize
                : AchievementToastViewModel.DefaultToastIconSize);
            set => IconSizeText = FormatSliderValue(value);
        }

        public double RarityBadgeSizeSlider
        {
            get => Surface?.RarityBadgeSize ??
                EffectiveTitleFontSize * AchievementToastViewModel.BadgeToTitleRatio;
            set => RarityBadgeSizeText = FormatSliderValue(value);
        }

        public double RarityFontSizeSlider
        {
            get => Surface?.RarityFontSize ?? (IsFrameSurface
                ? AchievementToastViewModel.FrameHeaderFontFallback
                : AchievementToastViewModel.DefaultToastCaptionFontSize);
            set => RarityFontSizeText = FormatSliderValue(value);
        }

        public double ProviderIconSizeSlider
        {
            get => Surface?.ProviderIconSize ?? (IsFrameSurface
                ? AchievementToastViewModel.DefaultFrameProviderIconSize
                : AchievementToastViewModel.DefaultToastProviderIconSize);
            set => ProviderIconSizeText = FormatSliderValue(value);
        }

        public double LinePaddingSlider
        {
            get => Surface?.LinePadding ?? 0;
            set => LinePaddingText = FormatOptionalSliderValue(value);
        }

        public double CardWidthSlider
        {
            get => Surface?.CardWidth ?? AchievementToastViewModel.DefaultToastCardWidth;
            set => CardWidthText = FormatSliderValue(value);
        }

        // Zero means "auto height" (the renderer's default), so the slider's minimum clears
        // the override instead of storing an explicit value.
        public double CardHeightSlider
        {
            get => Surface?.CardHeight ?? 0;
            set => CardHeightText = FormatOptionalSliderValue(value);
        }

        public double CardPaddingLeftSlider
        {
            get => Surface?.CardPaddingLeft ?? 0;
            set => CardPaddingLeftText = FormatOptionalSliderValue(value);
        }

        public double CardPaddingRightSlider
        {
            get => Surface?.CardPaddingRight ?? 0;
            set => CardPaddingRightText = FormatOptionalSliderValue(value);
        }

        // The title size the renderer would use, for the badge slider's default resting
        // position (badge default = title size x BadgeToTitleRatio).
        private double EffectiveTitleFontSize => Surface?.TitleFontSize ?? (IsFrameSurface
            ? AchievementToastViewModel.FrameTitleFontFallback
            : AchievementToastViewModel.DefaultToastTitleFontSize);

        private static string FormatSliderValue(double value) =>
            Math.Round(value).ToString(CultureInfo.CurrentCulture);

        // For fields whose renderer default is zero/none, dragging to the minimum clears the
        // override instead of storing an explicit 0 (which the size parser treats as invalid).
        private static string FormatOptionalSliderValue(double value) =>
            value <= 0 ? string.Empty : Math.Round(value).ToString(CultureInfo.CurrentCulture);

        // Anchor width used when fitting the card to a background image; mirrors the toast view
        // model's ToastCardWidth fallback so a blank width fits at the same size the card renders.
        private const double DefaultCardWidth = AchievementToastViewModel.DefaultToastCardWidth;

        /// <summary>
        /// Whether a toast background image is set. Drives the fit-to-image affordance, which is
        /// meaningless on the frame surface (no background) or with no image chosen.
        /// </summary>
        public bool HasBackgroundImage =>
            !IsFrameSurface && !string.IsNullOrWhiteSpace(_style?.ToastBackgroundImagePath);

        /// <summary>
        /// Cache-busted source for the editor's background thumbnail. The managed slot reuses a
        /// fixed filename, so picking a different image resolves to the same path; the write-time +
        /// size token makes the thumbnail re-decode the new file instead of showing the cached one.
        /// </summary>
        public string BackgroundThumbnailUri =>
            Models.Achievements.AchievementIconResolver.ApplyCacheBust(_style?.ToastBackgroundImagePath);

        // Cache-busted sources for the editor's badge thumbnails, for the same reason as the
        // background: the managed slots reuse fixed filenames, so imports and preset applies
        // overwrite the file without changing the stored path string.
        public string BadgeCommonThumbnailUri =>
            Models.Achievements.AchievementIconResolver.ApplyCacheBust(Surface?.BadgeImages?.CommonPath);

        public string BadgeUncommonThumbnailUri =>
            Models.Achievements.AchievementIconResolver.ApplyCacheBust(Surface?.BadgeImages?.UncommonPath);

        public string BadgeRareThumbnailUri =>
            Models.Achievements.AchievementIconResolver.ApplyCacheBust(Surface?.BadgeImages?.RarePath);

        public string BadgeUltraRareThumbnailUri =>
            Models.Achievements.AchievementIconResolver.ApplyCacheBust(Surface?.BadgeImages?.UltraRarePath);

        public string BadgeCompletionThumbnailUri =>
            Models.Achievements.AchievementIconResolver.ApplyCacheBust(Surface?.BadgeImages?.CompletionPath);

        private void RefreshBadgeThumbnails()
        {
            OnPropertyChanged(nameof(BadgeCommonThumbnailUri));
            OnPropertyChanged(nameof(BadgeUncommonThumbnailUri));
            OnPropertyChanged(nameof(BadgeRareThumbnailUri));
            OnPropertyChanged(nameof(BadgeUltraRareThumbnailUri));
            OnPropertyChanged(nameof(BadgeCompletionThumbnailUri));
        }

        /// <summary>
        /// The background image's pixel dimensions as "W × H" (empty when none is set), so the
        /// user can see what the "fit card to image" button sizes the card to.
        /// </summary>
        public string BackgroundImageDimensionsText =>
            HasBackgroundImage &&
            TryReadImagePixelSize(_style.ToastBackgroundImagePath, out var w, out var h)
                ? string.Format(CultureInfo.CurrentCulture, "{0} × {1}", w, h)
                : string.Empty;

        /// <summary>
        /// Sets the toast card's width and height to the background image's aspect ratio, keeping
        /// the current width as the anchor, so the UniformToFill background shows the whole image
        /// without cropping. Users still enter card dimensions manually; this just removes the
        /// guesswork of matching a specific image.
        /// </summary>
        public void FitCardToBackgroundImage()
        {
            var surface = Surface;
            if (surface == null || !_isEditable || !HasBackgroundImage)
            {
                return;
            }

            if (!TryReadImagePixelSize(_style.ToastBackgroundImagePath, out var imageWidth, out var imageHeight) ||
                imageWidth <= 0 || imageHeight <= 0)
            {
                return;
            }

            var width = surface.CardWidth is double w && w > 0 ? w : DefaultCardWidth;
            surface.CardWidth = width;
            surface.CardHeight = Math.Round(width * imageHeight / imageWidth);
            RefreshCardDimensions();
        }

        // Reads an image's pixel dimensions from its header without decoding the full bitmap.
        // Returns the first frame's size for animated GIFs (their logical canvas size).
        private static bool TryReadImagePixelSize(string path, out int width, out int height)
        {
            width = 0;
            height = 0;
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return false;
                }

                var frame = BitmapFrame.Create(
                    new Uri(path, UriKind.Absolute),
                    BitmapCreateOptions.DelayCreation,
                    BitmapCacheOption.None);
                width = frame.PixelWidth;
                height = frame.PixelHeight;
                return width > 0 && height > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Custom countdown-bar color hex, or blank to follow the default progress brush.
        /// Writing it updates the swatch and persists.
        /// </summary>
        public string CountdownBarColorText
        {
            get => Surface?.CountdownBarColor ?? string.Empty;
            set
            {
                var surface = Surface;
                if (surface == null || !_isEditable)
                {
                    return;
                }

                surface.CountdownBarColor = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
        }

        /// <summary>Preview swatch for the countdown-bar color (custom color, else default).</summary>
        public Brush CountdownBarSwatch
        {
            get
            {
                var color = Surface?.CountdownBarColor;
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
                        // Malformed stored color; show the default.
                    }
                }

                return System.Windows.Application.Current?.TryFindResource("PlayAch.Brush.Progress.Fill") as Brush
                       ?? Brushes.Gray;
            }
        }

        #endregion

        #region Header texts (pending until Apply; toast surface hosts the shared group)

        public string UnlockHeaderText
        {
            get => _unlockHeaderText;
            set => SetValue(ref _unlockHeaderText, value);
        }

        public string FriendUnlockHeaderText
        {
            get => _friendUnlockHeaderText;
            set => SetValue(ref _friendUnlockHeaderText, value);
        }

        public string CompletionHeaderText
        {
            get => _completionHeaderText;
            set => SetValue(ref _completionHeaderText, value);
        }

        public string FriendCompletionHeaderText
        {
            get => _friendCompletionHeaderText;
            set => SetValue(ref _friendCompletionHeaderText, value);
        }

        public bool HasHeaderFormatError
        {
            get => _hasHeaderFormatError;
            private set => SetValue(ref _hasHeaderFormatError, value);
        }

        /// <summary>
        /// Commits the pending header texts to this editor's surface. Blank or default-equal
        /// values store null (keep following the localized default). A non-blank friend format
        /// missing its {0} placeholder is kept pending and flagged instead of being stored.
        /// </summary>
        public void ApplyHeaderTexts()
        {
            var texts = Surface?.HeaderTexts;
            if (texts == null || !_isEditable)
            {
                return;
            }

            var hasError = false;

            texts.UnlockHeader = NotificationHeaderTextService.NormalizeForStore(
                UnlockHeaderText, NotificationHeaderTextService.GetDefaultUnlockHeader());
            texts.CompletionHeader = NotificationHeaderTextService.NormalizeForStore(
                CompletionHeaderText, NotificationHeaderTextService.GetDefaultCompletionHeader());

            if (string.IsNullOrWhiteSpace(FriendUnlockHeaderText) ||
                NotificationHeaderTextService.IsValidHeaderFormat(FriendUnlockHeaderText))
            {
                texts.FriendUnlockHeaderFormat = NotificationHeaderTextService.NormalizeForStore(
                    FriendUnlockHeaderText, NotificationHeaderTextService.GetDefaultFriendUnlockHeaderFormat());
            }
            else
            {
                hasError = true;
            }

            if (string.IsNullOrWhiteSpace(FriendCompletionHeaderText) ||
                NotificationHeaderTextService.IsValidHeaderFormat(FriendCompletionHeaderText))
            {
                texts.FriendCompletionHeaderFormat = NotificationHeaderTextService.NormalizeForStore(
                    FriendCompletionHeaderText, NotificationHeaderTextService.GetDefaultFriendCompletionHeaderFormat());
            }
            else
            {
                hasError = true;
            }

            HasHeaderFormatError = hasError;
            RefreshHeaderTexts(keepInvalidPending: true);
        }

        private void RefreshHeaderTexts(bool keepInvalidPending = false)
        {
            var texts = Surface?.HeaderTexts;
            UnlockHeaderText = texts?.UnlockHeader
                ?? NotificationHeaderTextService.GetDefaultUnlockHeader();
            CompletionHeaderText = texts?.CompletionHeader
                ?? NotificationHeaderTextService.GetDefaultCompletionHeader();

            if (!keepInvalidPending || !HasHeaderFormatError)
            {
                FriendUnlockHeaderText = texts?.FriendUnlockHeaderFormat
                    ?? NotificationHeaderTextService.GetDefaultFriendUnlockHeaderFormat();
                FriendCompletionHeaderText = texts?.FriendCompletionHeaderFormat
                    ?? NotificationHeaderTextService.GetDefaultFriendCompletionHeaderFormat();
            }
        }

        #endregion

        #region Images (this surface's badge images, plus the toast-owned background)

        /// <summary>
        /// Maps a logical slot (as named by the editor XAML's Tag attributes, which serve both
        /// surfaces) onto this surface's concrete store slot: badge slots resolve to the
        /// frame's own slots on the frame editor. The background is toast-only and passes
        /// through.
        /// </summary>
        private NotificationImageSlot ResolveSurfaceSlot(NotificationImageSlot slot)
        {
            if (!IsFrameSurface)
            {
                return slot;
            }

            switch (slot)
            {
                case NotificationImageSlot.BadgeCommon:
                    return NotificationImageSlot.FrameBadgeCommon;
                case NotificationImageSlot.BadgeUncommon:
                    return NotificationImageSlot.FrameBadgeUncommon;
                case NotificationImageSlot.BadgeRare:
                    return NotificationImageSlot.FrameBadgeRare;
                case NotificationImageSlot.BadgeUltraRare:
                    return NotificationImageSlot.FrameBadgeUltraRare;
                case NotificationImageSlot.BadgeCompletion:
                    return NotificationImageSlot.FrameBadgeCompletion;
                default:
                    return slot;
            }
        }

        /// <summary>
        /// Copies the picked file or URL into managed storage for the slot and stores the
        /// resulting path on the style. No-ops when materialization fails.
        /// </summary>
        public async Task ApplyImageAsync(NotificationImageSlot slot, string sourcePathOrUrl)
        {
            if (_style == null || !_isEditable)
            {
                return;
            }

            slot = ResolveSurfaceSlot(slot);
            try
            {
                // Clear the existing slot first so the UI releases the old file and the managed
                // slot starts empty before the new selection is materialized.
                _plugin.NotificationImageStore.DeleteSlot(_imageOwner, slot);
                SetImagePath(slot, null);

                var resolved = await _plugin.NotificationImageStore.MaterializeAsync(
                    sourcePathOrUrl, _imageOwner, slot, CancellationToken.None);
                if (resolved != null)
                {
                    // The managed slot uses a fixed filename, so picking a different source file
                    // resolves to the same path and would otherwise show the previously cached
                    // bitmap. Evict BEFORE setting the path: setting it synchronously triggers the
                    // preview/mockup reload (binding -> AsyncImage -> GetAsync), whose cache lookup
                    // must see an already-cleared cache to re-read the new file from disk.
                    _plugin.ImageService?.EvictByUriSegment(resolved);
                    SetImagePath(slot, resolved);
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed to apply notification image for slot {slot}.");
            }
        }

        /// <summary>
        /// Deletes the slot's managed image files and clears the stored path.
        /// </summary>
        public void ClearImage(NotificationImageSlot slot)
        {
            if (_style == null || !_isEditable)
            {
                return;
            }

            slot = ResolveSurfaceSlot(slot);
            var previous = GetImagePath(slot);
            _plugin.NotificationImageStore.DeleteSlot(_imageOwner, slot);
            SetImagePath(slot, null);

            // Drop the removed slot's bitmap so a later re-pick at the same managed path does
            // not resurface it from the memory cache.
            if (!string.IsNullOrEmpty(previous))
            {
                _plugin.ImageService?.EvictByUriSegment(previous);
            }
        }

        private string GetImagePath(NotificationImageSlot slot) =>
            NotificationImageSlotMap.GetPath(_style, slot);

        private void SetImagePath(NotificationImageSlot slot, string path) =>
            NotificationImageSlotMap.SetPath(_style, slot, path);

        #endregion

        #region Line order and sizes

        /// <summary>
        /// Moves the dragged line kinds before or after the target kind and stores the new
        /// order. Returns true when the order changed.
        /// </summary>
        public bool MoveLines(List<string> draggedKinds, string targetKind, bool insertAfter)
        {
            return ApplyLineOrder(order =>
            {
                var dragged = order
                    .Where(kind => draggedKinds.Contains(kind, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                if (dragged.Count == 0 ||
                    dragged.Contains(targetKind, StringComparer.OrdinalIgnoreCase))
                {
                    return null;
                }

                var remaining = order.Except(dragged, StringComparer.OrdinalIgnoreCase).ToList();
                var targetIndex = remaining.FindIndex(kind =>
                    string.Equals(kind, targetKind, StringComparison.OrdinalIgnoreCase));
                if (targetIndex < 0)
                {
                    return null;
                }

                remaining.InsertRange(insertAfter ? targetIndex + 1 : targetIndex, dragged);
                return remaining;
            });
        }

        /// <summary>
        /// Moves the dragged line kinds to the end of the order. Returns true when changed.
        /// </summary>
        public bool MoveLinesToEnd(List<string> draggedKinds)
        {
            return ApplyLineOrder(order =>
            {
                var dragged = order
                    .Where(kind => draggedKinds.Contains(kind, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                if (dragged.Count == 0)
                {
                    return null;
                }

                var remaining = order.Except(dragged, StringComparer.OrdinalIgnoreCase).ToList();
                remaining.AddRange(dragged);
                return remaining;
            });
        }

        private bool ApplyLineOrder(Func<List<string>, List<string>> reorder)
        {
            var surface = Surface;
            if (surface == null || !_isEditable)
            {
                return false;
            }

            var order = NotificationSurfaceStyle.CanonicalizeLineOrder(surface.LineOrder);
            var next = reorder(order);
            if (next == null || next.SequenceEqual(order, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            surface.LineOrder = new List<string>(next);
            return true;
        }

        private void OnLineRowSizeEdited(NotificationLineRowItem row, string text)
        {
            var surface = Surface;
            if (surface == null || !_isEditable)
            {
                SyncLineRows();
                return;
            }

            double? size = null;
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var current) &&
                    current > 0)
                {
                    size = current;
                }
                else if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var invariant) &&
                         invariant > 0)
                {
                    size = invariant;
                }
            }

            SetLineSize(surface, row.Kind, size);
            RefreshLineSizes();
        }

        private static void SetLineSize(NotificationSurfaceStyle surface, string kind, double? size)
        {
            switch (kind)
            {
                case NotificationSurfaceStyle.LineHeader:
                    surface.HeaderFontSize = size;
                    break;
                case NotificationSurfaceStyle.LineGameCategory:
                    surface.GameCategoryFontSize = size;
                    break;
                case NotificationSurfaceStyle.LineTitle:
                    surface.TitleFontSize = size;
                    break;
                case NotificationSurfaceStyle.LineDescription:
                    surface.BodyFontSize = size;
                    break;
            }
        }

        private static double? GetLineSize(NotificationSurfaceStyle surface, string kind)
        {
            switch (kind)
            {
                case NotificationSurfaceStyle.LineHeader:
                    return surface?.HeaderFontSize;
                case NotificationSurfaceStyle.LineGameCategory:
                    return surface?.GameCategoryFontSize;
                case NotificationSurfaceStyle.LineTitle:
                    return surface?.TitleFontSize;
                case NotificationSurfaceStyle.LineDescription:
                    return surface?.BodyFontSize;
                default:
                    return null;
            }
        }

        /// <summary>
        /// Reorders the stable row instances to match the stored line order (preserving grid
        /// selection) and refreshes their size texts.
        /// </summary>
        private void SyncLineRows()
        {
            var order = NotificationSurfaceStyle.CanonicalizeLineOrder(Surface?.LineOrder);
            for (var target = 0; target < order.Count && target < LineRows.Count; target++)
            {
                var current = -1;
                for (var i = target; i < LineRows.Count; i++)
                {
                    if (string.Equals(LineRows[i].Kind, order[target], StringComparison.OrdinalIgnoreCase))
                    {
                        current = i;
                        break;
                    }
                }

                if (current > target)
                {
                    LineRows.Move(current, target);
                }
            }

            RefreshLineSizes();
        }

        private void RefreshLineSizes()
        {
            var surface = Surface;
            foreach (var row in LineRows)
            {
                var size = GetLineSize(surface, row.Kind);
                row.UpdateSizeText(size?.ToString(CultureInfo.CurrentCulture) ?? string.Empty);
            }
        }

        private static string BuildLineDisplayName(string kind)
        {
            switch (kind)
            {
                case NotificationSurfaceStyle.LineHeader:
                    return L("LOCPlayAch_Settings_ToastShowHeader");
                case NotificationSurfaceStyle.LineTitle:
                    return L("LOCPlayAch_Settings_ToastShowName");
                case NotificationSurfaceStyle.LineDescription:
                    return L("LOCPlayAch_Settings_ToastShowDescription");
                case NotificationSurfaceStyle.LineGameCategory:
                    return L("LOCPlayAch_Settings_ToastShowGameName") + " / " + L("LOCPlayAch_Common_Label_Category");
                default:
                    return kind;
            }
        }

        #endregion

        /// <summary>
        /// Resets the edited surface (toast or frame) to its built-in factory default,
        /// discarding the user's style edits for that surface only, including its badge images
        /// and header texts. Replacing the surface object triggers the resubscribe, debounced
        /// persist, and mockup refresh through <see cref="OnStyleObjectPropertyChanged"/>; the
        /// surface-bound editor fields are refreshed here so the controls show the default
        /// values. The toast surface also owns the background image, so it is cleared there.
        /// No-op when nothing is loaded or the current selection is read-only.
        /// </summary>
        public void ResetSurfaceToDefault()
        {
            if (_style == null || !_isEditable)
            {
                return;
            }

            // Clear this surface's managed images before replacing the surface object, so the
            // old paths are still readable for memory-cache eviction. ClearImage maps the
            // logical badge slots onto this surface's own store slots.
            if (!IsFrameSurface)
            {
                ClearImage(NotificationImageSlot.Background);
            }

            ClearImage(NotificationImageSlot.BadgeCommon);
            ClearImage(NotificationImageSlot.BadgeUncommon);
            ClearImage(NotificationImageSlot.BadgeRare);
            ClearImage(NotificationImageSlot.BadgeUltraRare);
            ClearImage(NotificationImageSlot.BadgeCompletion);

            // A fresh surface carries fresh header texts too (empty store = localized default).
            if (IsFrameSurface)
            {
                _style.Frame = NotificationSurfaceStyle.CreateFrameDefault();
            }
            else
            {
                _style.Toast = NotificationSurfaceStyle.CreateToastDefault();
            }

            HasHeaderFormatError = false;
            RefreshHeaderTexts();

            SyncLineRows();
            RefreshCardDimensions();
            OnPropertyChanged(nameof(Surface));
            OnPropertyChanged(nameof(SelectedFontFamilyOption));
            OnPropertyChanged(nameof(SelectedBadgePlacement));
            OnPropertyChanged(nameof(SelectedPercentPlacement));
            OnPropertyChanged(nameof(IsProviderIconEnabled));
            OnPropertyChanged(nameof(SelectedGlowDisplay));
            OnPropertyChanged(nameof(SelectedFrameVignette));
            OnPropertyChanged(nameof(CountdownBarColorText));
            OnPropertyChanged(nameof(CountdownBarSwatch));
            OnPropertyChanged(nameof(TitleLineOffsetText));
            OnPropertyChanged(nameof(HasBackgroundImage));
            OnPropertyChanged(nameof(BackgroundImageDimensionsText));
            OnPropertyChanged(nameof(BackgroundThumbnailUri));
            RefreshBadgeThumbnails();
        }

        /// <summary>
        /// Points the editor at a new style object (global default or a provider copy).
        /// Pending edits against the previous style are flushed first.
        /// </summary>
        public void SetStyle(NotificationStyleSettings style, string providerKey, bool isEditable)
        {
            SetStyle(
                style,
                NotificationImageOwner.ForProvider(providerKey),
                isEditable,
                persistStyle: null,
                providerKey: providerKey);
        }

        /// <summary>
        /// Points the editor at an arbitrary owned style, allowing the shared editor surface to
        /// persist provider/global settings or a per-game custom-data snapshot through the same
        /// debounce path.
        /// </summary>
        public void SetStyle(
            NotificationStyleSettings style,
            NotificationImageOwner imageOwner,
            bool isEditable,
            Action<NotificationStyleSettings> persistStyle)
        {
            SetStyle(style, imageOwner, isEditable, persistStyle, providerKey: null);
        }

        private void SetStyle(
            NotificationStyleSettings style,
            NotificationImageOwner imageOwner,
            bool isEditable,
            Action<NotificationStyleSettings> persistStyle,
            string providerKey)
        {
            FlushPendingPersist();
            Unsubscribe();

            _style = style;
            _providerKey = string.IsNullOrWhiteSpace(providerKey) ? null : providerKey;
            _imageOwner = imageOwner ?? NotificationImageOwner.Global;
            _persistStyle = persistStyle;
            _isEditable = isEditable;

            Subscribe();
            SyncLineRows();
            RefreshCardDimensions();
            HasHeaderFormatError = false;
            RefreshHeaderTexts();

            OnPropertyChanged(nameof(Style));
            OnPropertyChanged(nameof(Surface));
            OnPropertyChanged(nameof(ProviderKey));
            OnPropertyChanged(nameof(IsEditable));
            OnPropertyChanged(nameof(SelectedFontFamilyOption));
            OnPropertyChanged(nameof(SelectedBadgePlacement));
            OnPropertyChanged(nameof(SelectedPercentPlacement));
            OnPropertyChanged(nameof(IsProviderIconEnabled));
            OnPropertyChanged(nameof(SelectedGlowDisplay));
            OnPropertyChanged(nameof(SelectedFrameVignette));
            OnPropertyChanged(nameof(CountdownBarColorText));
            OnPropertyChanged(nameof(CountdownBarSwatch));
            OnPropertyChanged(nameof(HasBackgroundImage));
            OnPropertyChanged(nameof(BackgroundImageDimensionsText));
            OnPropertyChanged(nameof(BackgroundThumbnailUri));
            OnPropertyChanged(nameof(TitleLineOffsetText));
            RefreshBadgeThumbnails();
        }

        private void Subscribe()
        {
            if (_style == null)
            {
                return;
            }

            _style.PropertyChanged += OnStyleObjectPropertyChanged;
            _subscribedSurface = Surface;
            if (_subscribedSurface != null)
            {
                _subscribedSurface.PropertyChanged += OnSurfacePropertyChanged;

                // Each editor persists edits to its own surface's badge images and header
                // texts; the two surfaces are fully independent objects.
                _subscribedBadges = _subscribedSurface.BadgeImages;
                _subscribedBadges.PropertyChanged += OnStyleObjectPropertyChanged;
                _subscribedHeaderTexts = _subscribedSurface.HeaderTexts;
                _subscribedHeaderTexts.PropertyChanged += OnStyleObjectPropertyChanged;
            }
        }

        private void Unsubscribe()
        {
            if (_style != null)
            {
                _style.PropertyChanged -= OnStyleObjectPropertyChanged;
            }

            if (_subscribedSurface != null)
            {
                _subscribedSurface.PropertyChanged -= OnSurfacePropertyChanged;
                _subscribedSurface = null;
            }

            if (_subscribedBadges != null)
            {
                _subscribedBadges.PropertyChanged -= OnStyleObjectPropertyChanged;
                _subscribedBadges = null;
            }

            if (_subscribedHeaderTexts != null)
            {
                _subscribedHeaderTexts.PropertyChanged -= OnStyleObjectPropertyChanged;
                _subscribedHeaderTexts = null;
            }
        }

        private void OnStyleObjectPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (ReferenceEquals(sender, _style) &&
                (e.PropertyName == nameof(NotificationStyleSettings.Toast) ||
                 e.PropertyName == nameof(NotificationStyleSettings.Frame)))
            {
                // The surface object was replaced wholesale (reset, import, preset apply);
                // resubscribe to the new instance and its nested groups, and re-snapshot the
                // surface-derived mirrors so the editor never shows the old surface's values.
                Unsubscribe();
                Subscribe();
                RefreshHeaderTexts();
                RefreshBadgeThumbnails();
            }

            if (ReferenceEquals(sender, _style) &&
                e.PropertyName == nameof(NotificationStyleSettings.ToastBackgroundImagePath))
            {
                OnPropertyChanged(nameof(Style));
                OnPropertyChanged(nameof(HasBackgroundImage));
                OnPropertyChanged(nameof(BackgroundImageDimensionsText));
                OnPropertyChanged(nameof(BackgroundThumbnailUri));
            }

            if (ReferenceEquals(sender, _subscribedBadges))
            {
                RefreshBadgeThumbnails();
            }

            NotifyStyleEdited();
        }

        private void OnSurfacePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(NotificationSurfaceStyle.LineOrder))
            {
                SyncLineRows();
            }
            else if (e.PropertyName == nameof(NotificationSurfaceStyle.HeaderFontSize) ||
                     e.PropertyName == nameof(NotificationSurfaceStyle.TitleFontSize) ||
                     e.PropertyName == nameof(NotificationSurfaceStyle.BodyFontSize) ||
                     e.PropertyName == nameof(NotificationSurfaceStyle.GameCategoryFontSize))
            {
                RefreshLineSizes();

                // The badge slider's default resting position tracks the title size.
                OnPropertyChanged(nameof(RarityBadgeSizeSlider));
            }
            else if (e.PropertyName == nameof(NotificationSurfaceStyle.FontFamily))
            {
                OnPropertyChanged(nameof(SelectedFontFamilyOption));
            }
            else if (e.PropertyName == nameof(NotificationSurfaceStyle.CardWidth) ||
                     e.PropertyName == nameof(NotificationSurfaceStyle.CardHeight))
            {
                RefreshCardDimensions();
            }
            else if (e.PropertyName == nameof(NotificationSurfaceStyle.TitleLineOffset))
            {
                OnPropertyChanged(nameof(TitleLineOffsetText));
            }
            else if (e.PropertyName == nameof(NotificationSurfaceStyle.CountdownBarColor))
            {
                OnPropertyChanged(nameof(CountdownBarColorText));
                OnPropertyChanged(nameof(CountdownBarSwatch));
            }
            else if (e.PropertyName == nameof(NotificationSurfaceStyle.ShowRarityBadge) ||
                     e.PropertyName == nameof(NotificationSurfaceStyle.ShowRarityPercent) ||
                     e.PropertyName == nameof(NotificationSurfaceStyle.InlineRarityBadge) ||
                     e.PropertyName == nameof(NotificationSurfaceStyle.RightRarityBadge) ||
                     e.PropertyName == nameof(NotificationSurfaceStyle.RarityPercentUnderBadge))
            {
                OnPropertyChanged(nameof(SelectedBadgePlacement));
                OnPropertyChanged(nameof(SelectedPercentPlacement));
                OnPropertyChanged(nameof(IsProviderIconEnabled));
            }
            else if (e.PropertyName == nameof(NotificationSurfaceStyle.ShowRarityGlow) ||
                     e.PropertyName == nameof(NotificationSurfaceStyle.NotificationBorderGlow))
            {
                OnPropertyChanged(nameof(SelectedGlowDisplay));
            }
            else if (e.PropertyName == nameof(NotificationSurfaceStyle.FrameVignette))
            {
                OnPropertyChanged(nameof(SelectedFrameVignette));
            }
            else if (e.PropertyName == nameof(NotificationSurfaceStyle.BadgeImages) ||
                     e.PropertyName == nameof(NotificationSurfaceStyle.HeaderTexts))
            {
                // A nested group object was replaced wholesale (import, preset apply);
                // resubscribe to the new instance and re-snapshot the mirrors and thumbnails.
                Unsubscribe();
                Subscribe();
                RefreshHeaderTexts();
                RefreshBadgeThumbnails();
            }

            NotifyStyleEdited();
        }

        private void NotifyStyleEdited()
        {
            if (!_isEditable)
            {
                return;
            }

            SchedulePersist();
            StyleChanged?.Invoke(this, EventArgs.Empty);
        }

        private void SchedulePersist()
        {
            _hasPendingPersist = true;
            _persistDebounceTimer.Stop();
            _persistDebounceTimer.Start();
        }

        private void OnPersistDebounceTimerTick(object sender, EventArgs e)
        {
            FlushPendingPersist();
        }

        /// <summary>
        /// Persists a pending debounced change immediately. Called from the debounce timer
        /// tick, before style swaps, and from Dispose so closing settings never drops an edit.
        /// </summary>
        public void FlushPendingPersist()
        {
            _persistDebounceTimer.Stop();
            if (!_hasPendingPersist)
            {
                return;
            }

            _hasPendingPersist = false;
            try
            {
                if (_persistStyle != null)
                {
                    _persistStyle(_style);
                }
                else
                {
                    if (_providerKey != null && _style != null)
                    {
                        // Re-store the edited copy; the setter clones it and raises
                        // PropertyChanged(ProviderNotificationStyles).
                        _settings.Persisted?.SetProviderNotificationStyle(_providerKey, _style);
                    }

                    _plugin.PersistSettingsForUi();
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to persist notification appearance settings.");
            }
        }

        /// <summary>
        /// Drops a queued debounce without writing it. Used when an external whole-game import
        /// or clear has already replaced the authoritative custom-data row.
        /// </summary>
        public void DiscardPendingPersist()
        {
            _persistDebounceTimer.Stop();
            _hasPendingPersist = false;
        }

        public void Dispose()
        {
            FlushPendingPersist();
            _persistDebounceTimer.Tick -= OnPersistDebounceTimerTick;
            Unsubscribe();
        }

        private static IReadOnlyList<FontFamilyOption> BuildFontFamilyOptions()
        {
            var options = new List<FontFamilyOption>
            {
                new FontFamilyOption(
                    L("LOCPlayAch_Settings_Style_FontThemeDefault"),
                    familyName: null,
                    previewFamily: System.Windows.SystemFonts.MessageFontFamily)
            };

            options.AddRange(Fonts.SystemFontFamilies
                .Select(family => new FontFamilyOption(family.Source, family.Source, family))
                .OrderBy(option => option.DisplayName, StringComparer.CurrentCultureIgnoreCase));

            return options;
        }

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
        }
    }

    /// <summary>
    /// Glow placement choices offered by the single "Glow" dropdown.
    /// </summary>
    internal enum GlowDisplay
    {
        Icon,
        Notification,
        Both,
        None
    }

    /// <summary>
    /// One entry of the glow display dropdown: the placement value and its localized label.
    /// </summary>
    internal sealed class GlowDisplayOption
    {
        public GlowDisplayOption(GlowDisplay value, string display)
        {
            Value = value;
            Display = display;
        }

        public GlowDisplay Value { get; }

        public string Display { get; }
    }

    /// <summary>
    /// One entry of the frame vignette dropdown: the value and its localized label.
    /// </summary>
    internal sealed class FrameVignetteOption
    {
        public FrameVignetteOption(FrameVignetteStyle value, string display)
        {
            Value = value;
            Display = display;
        }

        public FrameVignetteStyle Value { get; }

        public string Display { get; }
    }

    /// <summary>
    /// Rarity badge placement offered by the "Rarity badge" dropdown.
    /// </summary>
    internal enum RarityBadgePlacement
    {
        None,
        UnderIcon,
        Inline,
        Right
    }

    /// <summary>
    /// Rarity percent placement offered by the "Rarity percent" dropdown.
    /// </summary>
    internal enum RarityPercentPlacement
    {
        None,
        UnderIcon,
        WithBadge
    }

    /// <summary>
    /// One entry of the rarity badge placement dropdown: the value and its localized label.
    /// </summary>
    internal sealed class RarityBadgePlacementOption
    {
        public RarityBadgePlacementOption(RarityBadgePlacement value, string display)
        {
            Value = value;
            Display = display;
        }

        public RarityBadgePlacement Value { get; }

        public string Display { get; }
    }

    /// <summary>
    /// One entry of the rarity percent placement dropdown: the value and its localized label.
    /// </summary>
    internal sealed class RarityPercentPlacementOption
    {
        public RarityPercentPlacementOption(RarityPercentPlacement value, string display)
        {
            Value = value;
            Display = display;
        }

        public RarityPercentPlacement Value { get; }

        public string Display { get; }
    }

    /// <summary>
    /// One entry of the font family picker. A null <see cref="FamilyName"/> means the theme
    /// default; <see cref="PreviewFamily"/> is never null so item rendering has a valid font.
    /// </summary>
    internal sealed class FontFamilyOption
    {
        public FontFamilyOption(string displayName, string familyName, FontFamily previewFamily)
        {
            DisplayName = displayName;
            FamilyName = familyName;
            PreviewFamily = previewFamily;
        }

        public string DisplayName { get; }

        public string FamilyName { get; }

        public FontFamily PreviewFamily { get; }
    }

    /// <summary>
    /// One draggable text line row of the appearance editor: the line kind token, its
    /// localized name, and its editable font size text (blank = theme default).
    /// </summary>
    internal sealed class NotificationLineRowItem : ObservableObject
    {
        private readonly Action<NotificationLineRowItem, string> _onSizeEdited;
        private string _sizeText = string.Empty;

        public NotificationLineRowItem(
            string kind,
            string displayName,
            Action<NotificationLineRowItem, string> onSizeEdited)
        {
            Kind = kind;
            DisplayName = displayName;
            _onSizeEdited = onSizeEdited;
        }

        public string Kind { get; }

        public string DisplayName { get; }

        /// <summary>
        /// Editable size text (LostFocus commit). Setting it routes through the owner, which
        /// parses and stores the size; the displayed text is then refreshed via
        /// <see cref="UpdateSizeText"/> so invalid input snaps back to the stored value.
        /// </summary>
        public string SizeText
        {
            get => _sizeText;
            set
            {
                if (!SetValueAndReturn(ref _sizeText, value))
                {
                    return;
                }

                _onSizeEdited?.Invoke(this, value);
            }
        }

        /// <summary>
        /// Updates the displayed size text without routing back through the edit callback.
        /// </summary>
        public void UpdateSizeText(string value)
        {
            SetValue(ref _sizeText, value, nameof(SizeText));
        }
    }
}
