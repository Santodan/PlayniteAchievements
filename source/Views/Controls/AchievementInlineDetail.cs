using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.Views.Converters;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// One rarity, trophy or points detail drawn in front of an achievement's name or description.
    /// </summary>
    /// <remarks>
    /// <see cref="Styles"/> is the global detail priority (the enabled styles of
    /// <see cref="PersistedSettings.ProgressDetails"/>), empty while the grid's spot is off.
    /// The element shows the first style the achievement has data for, and measures to nothing
    /// when it has none, so the name and description spots always show the same detail.
    /// Rarity and trophy draw their badge; points draw the family's icon, the value and a dash.
    /// Text takes the font and color the hosting TextBlock passes down, so a points value reads
    /// as part of the name. The DataContext is the row's <see cref="AchievementDisplayItem"/>,
    /// read only for the tooltip.
    /// </remarks>
    public sealed class AchievementInlineDetail : FrameworkElement
    {
        private const double IconGap = 4;
        private const double TextGap = 3;
        private const double DefaultIconSize = 16;
        private const string PointsSeparator = " – ";

        private ProgressDetailStyle? _style;
        private ImageSource _image;
        private FormattedText _text;
        private double _iconSize;
        private double _iconWidth;

        public static readonly DependencyProperty StylesProperty = Register(nameof(Styles), typeof(IReadOnlyList<ProgressDetailStyle>), null);
        public static readonly DependencyProperty RarityProperty = Register(nameof(Rarity), typeof(RarityTier), RarityTier.Common);
        public static readonly DependencyProperty HasRarityProperty = Register(nameof(HasRarity), typeof(bool), false);
        public static readonly DependencyProperty ShowsRarityProperty = Register(nameof(ShowsRarity), typeof(bool), false);
        public static readonly DependencyProperty IsRarityHiddenProperty = Register(nameof(IsRarityHidden), typeof(bool), false);
        public static readonly DependencyProperty TrophyTypeProperty = Register(nameof(TrophyType), typeof(string), null);
        public static readonly DependencyProperty PointsTextProperty = Register(nameof(PointsText), typeof(string), null);
        public static readonly DependencyProperty HasPointsProperty = Register(nameof(HasPoints), typeof(bool), false);
        public static readonly DependencyProperty ProviderKeyProperty = Register(nameof(ProviderKey), typeof(string), null);

        private static DependencyProperty Register(string name, Type type, object defaultValue) =>
            DependencyProperty.Register(
                name,
                type,
                typeof(AchievementInlineDetail),
                new FrameworkPropertyMetadata(
                    defaultValue,
                    FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public IReadOnlyList<ProgressDetailStyle> Styles
        {
            get => (IReadOnlyList<ProgressDetailStyle>)GetValue(StylesProperty);
            set => SetValue(StylesProperty, value);
        }

        public RarityTier Rarity
        {
            get => (RarityTier)GetValue(RarityProperty);
            set => SetValue(RarityProperty, value);
        }

        /// <summary>Whether the achievement has a rarity percent.</summary>
        public bool HasRarity
        {
            get => (bool)GetValue(HasRarityProperty);
            set => SetValue(HasRarityProperty, value);
        }

        /// <summary>
        /// Whether the game's provider supplies rarity (a tier or a percent), so a tier without a
        /// percent is real rather than the Common default.
        /// </summary>
        public bool ShowsRarity
        {
            get => (bool)GetValue(ShowsRarityProperty);
            set => SetValue(ShowsRarityProperty, value);
        }

        /// <summary>Whether the rarity mask applies: the unknown-rarity badge stands in for the tier.</summary>
        public bool IsRarityHidden
        {
            get => (bool)GetValue(IsRarityHiddenProperty);
            set => SetValue(IsRarityHiddenProperty, value);
        }

        /// <summary>The resolved grade: <see cref="AchievementDisplayItem.MaskedTrophyType"/> while masked.</summary>
        public string TrophyType
        {
            get => (string)GetValue(TrophyTypeProperty);
            set => SetValue(TrophyTypeProperty, value);
        }

        /// <summary>The resolved value text: the placeholder while masked.</summary>
        public string PointsText
        {
            get => (string)GetValue(PointsTextProperty);
            set => SetValue(PointsTextProperty, value);
        }

        public bool HasPoints
        {
            get => (bool)GetValue(HasPointsProperty);
            set => SetValue(HasPointsProperty, value);
        }

        public string ProviderKey
        {
            get => (string)GetValue(ProviderKeyProperty);
            set => SetValue(ProviderKeyProperty, value);
        }

        public AchievementInlineDetail()
        {
            // A placeholder so ToolTipOpening fires; the text is resolved when it opens.
            ToolTip = string.Empty;
            ToolTipOpening += OnToolTipOpening;
        }

        // The inherited text properties are not this element's own, so a name color trigger or a
        // font change in the hosting TextBlock would otherwise leave stale points text.
        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.Property == TextElement.ForegroundProperty ||
                e.Property == TextElement.FontSizeProperty ||
                e.Property == TextElement.FontFamilyProperty ||
                e.Property == TextElement.FontWeightProperty ||
                e.Property == TextElement.FontStyleProperty)
            {
                InvalidateMeasure();
                InvalidateVisual();
            }
        }

        private bool HasData(ProgressDetailStyle style)
        {
            switch (style)
            {
                case ProgressDetailStyle.Rarity:
                    return HasRarity || ShowsRarity;
                case ProgressDetailStyle.Trophy:
                    return !string.IsNullOrWhiteSpace(TrophyType);
                case ProgressDetailStyle.Points:
                    return HasPoints;
                default:
                    return false;
            }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            _style = ProgressDetailOrder.PickInline(Styles, HasData);
            _image = null;
            _text = null;

            if (_style == null)
            {
                Effect = null;
                Cursor = null;
                return new Size(0, 0);
            }

            var fontSize = TextElement.GetFontSize(this);
            var tiny = TryFindResource("PlayAch.Size.Icon.Tiny") is double tinySize && tinySize > 0 ? tinySize : DefaultIconSize;
            _iconSize = Math.Min(tiny, Math.Round(fontSize * 1.3));

            switch (_style.Value)
            {
                case ProgressDetailStyle.Rarity:
                    _image = TryFindResource(IsRarityHidden ? "BadgeRarityUnknown" : RarityImageKey(Rarity)) as ImageSource;
                    break;
                case ProgressDetailStyle.Trophy:
                    _image = TryFindResource(TrophyImageKey(TrophyType)) as ImageSource;
                    break;
                case ProgressDetailStyle.Points:
                    var visuals = ProviderRegistry.ResolveProviderVisualsOrFallback(PointsFamilies.FromProviderKey(ProviderKey));
                    _image = ProviderIconConverter.BuildIcon(visuals.iconKey, visuals.colorHex);
                    _text = new FormattedText(
                        (PointsText ?? string.Empty) + PointsSeparator,
                        CultureInfo.CurrentUICulture,
                        FlowDirection,
                        new Typeface(
                            TextElement.GetFontFamily(this),
                            TextElement.GetFontStyle(this),
                            TextElement.GetFontWeight(this),
                            TextElement.GetFontStretch(this)),
                        fontSize,
                        TextElement.GetForeground(this) ?? Brushes.Gray,
                        VisualTreeHelper.GetDpi(this).PixelsPerDip);
                    break;
            }

            // The height is fixed to the text line; the width keeps the image's own proportions, so
            // a badge that is not square is not stretched into one.
            _iconWidth = _image != null && _image.Height > 0
                ? Math.Round(_iconSize * _image.Width / _image.Height)
                : _iconSize;

            // The badge shadow the rarity and trophy columns use; points text stays unshadowed.
            Effect = _style.Value == ProgressDetailStyle.Points ? null : TryFindResource("BadgeShadow") as Effect;
            Cursor = IsMasked ? Cursors.Hand : null;

            var width = _iconWidth + (_text != null ? TextGap + _text.WidthIncludingTrailingWhitespace : IconGap);
            var height = Math.Max(_iconSize, _text?.Height ?? 0);
            return new Size(width, height);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            if (_style == null)
            {
                return;
            }

            var height = RenderSize.Height;
            if (_image != null)
            {
                drawingContext.DrawImage(_image, new Rect(0, (height - _iconSize) / 2, _iconWidth, _iconSize));
            }

            if (_text != null)
            {
                drawingContext.DrawText(_text, new Point(_iconWidth + TextGap, (height - _text.Height) / 2));
            }
        }

        private bool IsMasked =>
            (_style == ProgressDetailStyle.Rarity && IsRarityHidden) ||
            (_style == ProgressDetailStyle.Trophy &&
             string.Equals(TrophyType, AchievementDisplayItem.MaskedTrophyType, StringComparison.OrdinalIgnoreCase)) ||
            (_style == ProgressDetailStyle.Points &&
             string.Equals(PointsText, AchievementDisplayItem.MaskedValuePlaceholder, StringComparison.Ordinal));

        private void OnToolTipOpening(object sender, ToolTipEventArgs e)
        {
            var text = ResolveToolTip();
            if (string.IsNullOrWhiteSpace(text))
            {
                e.Handled = true;
                return;
            }

            ToolTip = text;
        }

        private string ResolveToolTip()
        {
            if (_style == null)
            {
                return null;
            }

            if (IsMasked)
            {
                return ResourceProvider.GetString("LOCPlayAch_Achievements_ClickToReveal");
            }

            switch (_style.Value)
            {
                case ProgressDetailStyle.Rarity:
                    return (DataContext as AchievementDisplayItem)?.RarityDetailText;
                case ProgressDetailStyle.Trophy:
                    return TrophyNameKey(TrophyType) is string key ? ResourceProvider.GetString(key) : null;
                case ProgressDetailStyle.Points:
                    return PointsFamilyNames.GetLabel(PointsFamilies.FromProviderKey(ProviderKey));
                default:
                    return null;
            }
        }

        private static string RarityImageKey(RarityTier rarity)
        {
            switch (rarity)
            {
                case RarityTier.UltraRare:
                    return "BadgeRarityUltraRare";
                case RarityTier.Rare:
                    return "BadgeRarityRare";
                case RarityTier.Uncommon:
                    return "BadgeRarityUncommon";
                default:
                    return "BadgeRarityCommon";
            }
        }

        private static string TrophyImageKey(string trophyType)
        {
            switch (trophyType?.Trim().ToLowerInvariant())
            {
                case "platinum":
                    return "TrophyPlatinum";
                case "gold":
                    return "TrophyGold";
                case "silver":
                    return "TrophySilver";
                case "bronze":
                    return "TrophyBronze";
                default:
                    return "TrophyUnknown";
            }
        }

        private static string TrophyNameKey(string trophyType)
        {
            switch (trophyType?.Trim().ToLowerInvariant())
            {
                case "platinum":
                    return "LOCPlayAch_Trophy_Platinum";
                case "gold":
                    return "LOCPlayAch_Trophy_Gold";
                case "silver":
                    return "LOCPlayAch_Trophy_Silver";
                case "bronze":
                    return "LOCPlayAch_Trophy_Bronze";
                default:
                    return null;
            }
        }
    }
}
