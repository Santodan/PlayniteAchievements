using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// The rarity (or trophy) badge row under a game's progress bar, drawn as one element.
    /// </summary>
    /// <remarks>
    /// The element-per-badge template cost about 45 elements per row: eight StackPanels, eight
    /// Images each carrying its own shadow effect, eight TextBlocks with a per-tier color
    /// trigger, and a tooltip on each. With a dozen rows on screen that was the heaviest part of
    /// realizing a game summaries row and most of the render tick after it (one effect surface
    /// per badge). This draws the same icons and counts in OnRender with a single shadow surface
    /// for the icons, resolves its brushes, glyph images, and typeface from the same resource
    /// keys the old styles used, and serves the per-badge tooltips itself.
    /// The DataContext is the row's <see cref="GameSummaryItem"/>.
    /// </remarks>
    public sealed class ProgressBadgeStrip : FrameworkElement
    {
        // From the retired styles: item margin 4,0; count text margin 3,0,0,0; icon Icon.Tiny.
        private const double ItemSideMargin = 4;
        private const double TextGap = 3;
        private const double DefaultIconSize = 18;
        private const double DefaultFontSize = 12;

        private sealed class BadgeSpec
        {
            public string ImageKey;
            public string BrushKey;
            public string TooltipKey;
            public Func<GameSummaryItem, int> Count;
            public Func<GameSummaryItem, bool> Hidden;
        }

        // Descending rarity, the order the right-aligned footer shows; Reversed flips it.
        private static readonly BadgeSpec[] RaritySpecs =
        {
            new BadgeSpec { ImageKey = "BadgeRarityUltraRare", BrushKey = "PlayAch.Brush.Rarity.UltraRare", TooltipKey = "LOCPlayAch_Rarity_UltraRare", Count = item => item.UltraRareCount },
            new BadgeSpec { ImageKey = "BadgeRarityRare", BrushKey = "PlayAch.Brush.Rarity.Rare", TooltipKey = "LOCPlayAch_Rarity_Rare", Count = item => item.RareCount },
            new BadgeSpec { ImageKey = "BadgeRarityUncommon", BrushKey = "PlayAch.Brush.Rarity.Uncommon", TooltipKey = "LOCPlayAch_Rarity_Uncommon", Count = item => item.UncommonCount },
            new BadgeSpec { ImageKey = "BadgeRarityCommon", BrushKey = "PlayAch.Brush.Rarity.Common", TooltipKey = "LOCPlayAch_Rarity_Common", Count = item => item.CommonCount }
        };

        private static readonly BadgeSpec[] TrophySpecs =
        {
            // The platinum steps aside when the finish badge is showing it.
            new BadgeSpec { ImageKey = "TrophyPlatinum", BrushKey = "PlayAch.Brush.Trophy.Platinum", TooltipKey = "LOCPlayAch_Trophy_Platinum", Count = item => item.TrophyPlatinumCount, Hidden = item => item.ShowPlatinumInCompletionSpot },
            new BadgeSpec { ImageKey = "TrophyGold", BrushKey = "PlayAch.Brush.Trophy.Gold", TooltipKey = "LOCPlayAch_Trophy_Gold", Count = item => item.TrophyGoldCount },
            new BadgeSpec { ImageKey = "TrophySilver", BrushKey = "PlayAch.Brush.Trophy.Silver", TooltipKey = "LOCPlayAch_Trophy_Silver", Count = item => item.TrophySilverCount },
            new BadgeSpec { ImageKey = "TrophyBronze", BrushKey = "PlayAch.Brush.Trophy.Bronze", TooltipKey = "LOCPlayAch_Trophy_Bronze", Count = item => item.TrophyBronzeCount }
        };

        private struct BadgeLayout
        {
            public BadgeSpec Spec;
            public FormattedText Text;
            public double Left;
            public double Width;
        }

        public static readonly DependencyProperty PreferTrophyBadgesProperty =
            DependencyProperty.Register(
                nameof(PreferTrophyBadges),
                typeof(bool),
                typeof(ProgressBadgeStrip),
                new FrameworkPropertyMetadata(
                    false,
                    FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>Show PlayStation trophy badges instead of rarity badges when the game has trophies.</summary>
        public bool PreferTrophyBadges
        {
            get => (bool)GetValue(PreferTrophyBadgesProperty);
            set => SetValue(PreferTrophyBadgesProperty, value);
        }

        public static readonly DependencyProperty ColorByRarityProperty =
            DependencyProperty.Register(
                nameof(ColorByRarity),
                typeof(bool),
                typeof(ProgressBadgeStrip),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>Tint each count with its tier's color instead of the plain text brush.</summary>
        public bool ColorByRarity
        {
            get => (bool)GetValue(ColorByRarityProperty);
            set => SetValue(ColorByRarityProperty, value);
        }

        public static readonly DependencyProperty ReversedProperty =
            DependencyProperty.Register(
                nameof(Reversed),
                typeof(bool),
                typeof(ProgressBadgeStrip),
                new FrameworkPropertyMetadata(
                    false,
                    FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>Ascending rarity (left-aligned and centered footers) instead of descending.</summary>
        public bool Reversed
        {
            get => (bool)GetValue(ReversedProperty);
            set => SetValue(ReversedProperty, value);
        }

        // The icons render through this child so one shadow surface covers all of them, the way
        // each Image used to carry its own; the counts draw shadow-free in OnRender as before.
        private readonly DrawingVisual _iconVisual = new DrawingVisual();
        private readonly List<BadgeLayout> _layout = new List<BadgeLayout>(4);
        private GameSummaryItem _item;
        private bool _appearanceHooked;
        private bool _resourcesResolved;
        private Typeface _typeface;
        private double _fontSize;
        private double _iconSize;
        private double _pixelsPerDip;
        private Brush _textBrush;
        private readonly Dictionary<string, Brush> _tierBrushes = new Dictionary<string, Brush>(StringComparer.Ordinal);
        private readonly Dictionary<string, ImageSource> _images = new Dictionary<string, ImageSource>(StringComparer.Ordinal);

        public ProgressBadgeStrip()
        {
            AddVisualChild(_iconVisual);
            DataContextChanged += OnDataContextChanged;
            // Loaded re-fires when the row is recycled or the host is re-parented without a
            // guaranteed Unloaded; the guard keeps the static event from stacking handlers.
            Loaded += (_, __) =>
            {
                if (!_appearanceHooked)
                {
                    _appearanceHooked = true;
                    RarityAppearanceHelper.AppearanceChanged += OnAppearanceChanged;
                }
            };
            Unloaded += (_, __) =>
            {
                if (_appearanceHooked)
                {
                    _appearanceHooked = false;
                    RarityAppearanceHelper.AppearanceChanged -= OnAppearanceChanged;
                }

                CloseToolTip();
            };
        }

        protected override int VisualChildrenCount => 1;

        protected override Visual GetVisualChild(int index)
        {
            if (index != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return _iconVisual;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_item != null)
            {
                PropertyChangedEventManager.RemoveHandler(_item, OnItemPropertyChanged, string.Empty);
            }

            _item = e.NewValue as GameSummaryItem;
            if (_item != null)
            {
                PropertyChangedEventManager.AddHandler(_item, OnItemPropertyChanged, string.Empty);
            }

            InvalidateMeasure();
            InvalidateVisual();
        }

        private void OnItemPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Counts, totals and the derived flags all arrive through this; the invalidations
            // are idempotent so a burst of changes still costs one measure and one render.
            InvalidateMeasure();
            InvalidateVisual();
        }

        private void OnAppearanceChanged(object sender, EventArgs e)
        {
            _resourcesResolved = false;
            InvalidateMeasure();
            InvalidateVisual();
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            _resourcesResolved = false;
            InvalidateMeasure();
            InvalidateVisual();
        }

        // Brushes, glyph images, and the typeface are read from the same resource keys the old
        // styles used (recolors republish them and raise AppearanceChanged, which drops this
        // cache), once per strip rather than per badge per pass.
        private void EnsureResources()
        {
            if (_resourcesResolved)
            {
                return;
            }

            _resourcesResolved = true;
            _textBrush = TryFindResource("PlayAch.Brush.Text") as Brush ?? Brushes.Gray;
            _fontSize = TryFindResource("PlayAch.FontSize.Body") is double size && size > 0 ? size : DefaultFontSize;
            _iconSize = TryFindResource("PlayAch.Size.Icon.Tiny") is double icon && icon > 0 ? icon : DefaultIconSize;
            var fontFamily = TryFindResource("PlayAch.FontFamily.Body") as FontFamily
                             ?? TextElement.GetFontFamily(this)
                             ?? new FontFamily("Segoe UI");
            _typeface = new Typeface(fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            _iconVisual.Effect = TryFindResource("BadgeShadow") as Effect;

            _tierBrushes.Clear();
            _images.Clear();
            foreach (var spec in RaritySpecs)
            {
                CacheSpecResources(spec);
            }

            foreach (var spec in TrophySpecs)
            {
                CacheSpecResources(spec);
            }
        }

        private void CacheSpecResources(BadgeSpec spec)
        {
            _tierBrushes[spec.BrushKey] = TryFindResource(spec.BrushKey) as Brush;
            _images[spec.ImageKey] = TryFindResource(spec.ImageKey) as ImageSource;
        }

        private IEnumerable<BadgeSpec> VisibleSpecs()
        {
            var item = _item;
            if (item == null)
            {
                yield break;
            }

            var specs = item.HasTrophyTypes && PreferTrophyBadges ? TrophySpecs : RaritySpecs;
            var count = specs.Length;
            for (var step = 0; step < count; step++)
            {
                var spec = specs[Reversed ? count - 1 - step : step];
                if (spec.Count(item) <= 0 || spec.Hidden?.Invoke(item) == true)
                {
                    continue;
                }

                yield return spec;
            }
        }

        private void BuildLayout()
        {
            _layout.Clear();
            var item = _item;
            if (item == null)
            {
                return;
            }

            EnsureResources();
            var culture = FormattingCulture.Current ?? CultureInfo.CurrentCulture;
            var x = 0d;
            foreach (var spec in VisibleSpecs())
            {
                var brush = ColorByRarity && _tierBrushes.TryGetValue(spec.BrushKey, out var tier) && tier != null
                    ? tier
                    : _textBrush;
                var text = new FormattedText(
                    spec.Count(item).ToString("N0", culture),
                    culture,
                    FlowDirection.LeftToRight,
                    _typeface,
                    _fontSize,
                    brush,
                    _pixelsPerDip);
                var width = ItemSideMargin + _iconSize + TextGap + text.WidthIncludingTrailingWhitespace + ItemSideMargin;
                _layout.Add(new BadgeLayout { Spec = spec, Text = text, Left = x, Width = width });
                x += width;
            }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            BuildLayout();
            if (_layout.Count == 0)
            {
                return new Size(0, 0);
            }

            var width = 0d;
            var height = _iconSize;
            foreach (var badge in _layout)
            {
                width += badge.Width;
                height = Math.Max(height, badge.Text.Height);
            }

            return new Size(width, height);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var size = RenderSize;
            // A transparent fill gives the whole strip a hit-test surface for the tooltips.
            drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(size));
            if (_layout.Count == 0)
            {
                using (_iconVisual.RenderOpen())
                {
                }

                return;
            }

            var iconTop = Math.Max(0, (size.Height - _iconSize) / 2);
            using (var icons = _iconVisual.RenderOpen())
            {
                foreach (var badge in _layout)
                {
                    if (_images.TryGetValue(badge.Spec.ImageKey, out var image) && image != null)
                    {
                        icons.DrawImage(image, FitUniform(image, badge.Left + ItemSideMargin, iconTop, _iconSize));
                    }
                }
            }

            foreach (var badge in _layout)
            {
                var textTop = Math.Max(0, (size.Height - badge.Text.Height) / 2);
                drawingContext.DrawText(
                    badge.Text,
                    new Point(badge.Left + ItemSideMargin + _iconSize + TextGap, textTop));
            }
        }

        // DrawImage fills its rect; the Image elements this replaces used Stretch=Uniform, so a
        // glyph that is not square is scaled to fit the box and centered rather than stretched.
        private static Rect FitUniform(ImageSource image, double left, double top, double box)
        {
            var width = image.Width;
            var height = image.Height;
            if (width <= 0 || height <= 0 || double.IsNaN(width) || double.IsNaN(height))
            {
                return new Rect(left, top, box, box);
            }

            var scale = Math.Min(box / width, box / height);
            var drawnWidth = width * scale;
            var drawnHeight = height * scale;
            return new Rect(
                left + (box - drawnWidth) / 2,
                top + (box - drawnHeight) / 2,
                drawnWidth,
                drawnHeight);
        }

        private ToolTip _toolTip;
        private int _hoverIndex = -1;

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var x = e.GetPosition(this).X;
            var index = -1;
            for (var i = 0; i < _layout.Count; i++)
            {
                if (x >= _layout[i].Left && x < _layout[i].Left + _layout[i].Width)
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                _hoverIndex = -1;
                CloseToolTip();
                return;
            }

            if (index == _hoverIndex)
            {
                return;
            }

            _hoverIndex = index;
            var badge = _layout[index];
            ShowToolTip(
                ResourceProvider.GetString(badge.Spec.TooltipKey),
                new Rect(badge.Left, 0, badge.Width, RenderSize.Height));
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverIndex = -1;
            CloseToolTip();
        }

        // The tooltip service fixes a popup's position when it opens, so over one element it
        // would sit still while the pointer crosses badges; the strip owns one ToolTip and
        // reopens it against the hovered badge's rect instead.
        private void ShowToolTip(string content, Rect badgeRect)
        {
            if (string.IsNullOrEmpty(content))
            {
                CloseToolTip();
                return;
            }

            if (_toolTip == null)
            {
                _toolTip = new ToolTip
                {
                    PlacementTarget = this,
                    Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
                };
            }

            _toolTip.IsOpen = false;
            _toolTip.Content = content;
            _toolTip.PlacementRectangle = badgeRect;
            _toolTip.IsOpen = true;
        }

        private void CloseToolTip()
        {
            if (_toolTip != null)
            {
                _toolTip.IsOpen = false;
            }
        }
    }
}
