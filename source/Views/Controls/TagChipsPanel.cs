using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.ViewModels.Tags;
using PlayniteAchievements.Views.Converters;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// An achievement's tags, drawn as the grid's <see cref="AchievementTagDisplay"/> asks: names,
    /// icons <see cref="IconSize"/> square (a tag without one keeps its name) and tag colors. Commas
    /// separate only neighboring names; icons are spaced apart. An icon's tooltip is its tag's name.
    /// Chips wrap onto lines aligned by <see cref="TextAlignment"/>, each chip centered in its line.
    /// Chips past the cell's width and <see cref="MaxContentHeight"/> give way to a "+N" chip whose
    /// tooltip names them. Text takes its font and color from the inherited <see cref="TextElement"/> properties.
    /// </summary>
    public sealed class TagChipsPanel : Panel
    {
        // One frozen mask per icon; the fill supplies the color.
        private static readonly ConcurrentDictionary<string, Brush> MaskByIcon =
            new ConcurrentDictionary<string, Brush>(StringComparer.OrdinalIgnoreCase);

        public static readonly DependencyProperty ChipsProperty =
            DependencyProperty.Register(nameof(Chips), typeof(IReadOnlyList<AchievementTagChip>), typeof(TagChipsPanel),
                new PropertyMetadata(null, OnContentChanged));

        public IReadOnlyList<AchievementTagChip> Chips
        {
            get => (IReadOnlyList<AchievementTagChip>)GetValue(ChipsProperty);
            set => SetValue(ChipsProperty, value);
        }

        public static readonly DependencyProperty DisplayProperty =
            DependencyProperty.Register(nameof(Display), typeof(AchievementTagDisplay), typeof(TagChipsPanel),
                new PropertyMetadata(AchievementTagDisplay.None, OnContentChanged));

        public AchievementTagDisplay Display
        {
            get => (AchievementTagDisplay)GetValue(DisplayProperty);
            set => SetValue(DisplayProperty, value);
        }

        public static readonly DependencyProperty IconSizeProperty =
            DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(TagChipsPanel),
                new PropertyMetadata(16.0, OnIconSizeChanged));

        public double IconSize
        {
            get => (double)GetValue(IconSizeProperty);
            set => SetValue(IconSizeProperty, value);
        }

        public static readonly DependencyProperty TextAlignmentProperty =
            DependencyProperty.Register(nameof(TextAlignment), typeof(TextAlignment), typeof(TagChipsPanel),
                new FrameworkPropertyMetadata(TextAlignment.Left, FrameworkPropertyMetadataOptions.AffectsArrange));

        public TextAlignment TextAlignment
        {
            get => (TextAlignment)GetValue(TextAlignmentProperty);
            set => SetValue(TextAlignmentProperty, value);
        }

        public static readonly DependencyProperty MaxContentHeightProperty =
            DependencyProperty.Register(nameof(MaxContentHeight), typeof(double), typeof(TagChipsPanel),
                new FrameworkPropertyMetadata(double.PositiveInfinity, FrameworkPropertyMetadataOptions.AffectsMeasure));

        /// <summary>
        /// The height the tags may take. An auto-height grid row measures its cells without a
        /// height limit, so the grid states it; tags past it give way to "+N".
        /// </summary>
        public double MaxContentHeight
        {
            get => (double)GetValue(MaxContentHeightProperty);
            set => SetValue(MaxContentHeightProperty, value);
        }

        // The "+N" chip, the last child whenever there are chips; shown only when some do not fit.
        private TextBlock _overflow;

        // How many leading chips the last measure found room for.
        private int _visibleCount;

        private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
            ((TagChipsPanel)d).Rebuild();

        private static void OnIconSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var panel = (TagChipsPanel)d;
            foreach (UIElement child in panel.InternalChildren)
            {
                if (child is Rectangle icon)
                {
                    icon.Width = panel.IconSize;
                    icon.Height = panel.IconSize;
                }
            }
        }

        private void Rebuild()
        {
            InternalChildren.Clear();
            _overflow = null;
            var chips = Chips;
            if (chips == null || chips.Count == 0)
            {
                return;
            }

            var colors = (Display & AchievementTagDisplay.Colors) != 0;
            for (var i = 0; i < chips.Count; i++)
            {
                var chip = chips[i];
                var brush = colors ? chip.Brush : null;
                var mask = IconMask(chip, Display);
                if (mask != null)
                {
                    InternalChildren.Add(CreateIcon(chip, mask, brush));
                    continue;
                }

                var text = new TextBlock
                {
                    Text = TextOf(chips, i, Display),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                if (brush != null)
                {
                    text.Foreground = brush;
                }

                InternalChildren.Add(text);
            }

            _overflow = new TextBlock { FontWeight = FontWeights.SemiBold };
            InternalChildren.Add(_overflow);
        }

        /// <summary>True when <paramref name="display"/> draws <paramref name="chip"/> as its icon.</summary>
        public static bool IsIcon(AchievementTagChip chip, AchievementTagDisplay display) =>
            IconMask(chip, display) != null;

        /// <summary>
        /// The text a name chip shows: its name, with a comma when the next chip is a name too.
        /// </summary>
        public static string TextOf(IReadOnlyList<AchievementTagChip> chips, int index, AchievementTagDisplay display) =>
            index + 1 < chips.Count && !IsIcon(chips[index + 1], display)
                ? chips[index].Name + ","
                : chips[index].Name;

        private static Brush IconMask(AchievementTagChip chip, AchievementTagDisplay display)
        {
            if ((display & AchievementTagDisplay.Icons) == 0 || chip?.Icon == null)
            {
                return null;
            }

            if (MaskByIcon.TryGetValue(chip.Icon, out var mask))
            {
                return mask;
            }

            // A glyph that fails to build stays uncached: the icon font may not be loaded yet.
            mask = BuildMask(chip.Icon);
            return mask == null ? null : MaskByIcon.GetOrAdd(chip.Icon, mask);
        }

        private Rectangle CreateIcon(AchievementTagChip chip, Brush mask, Brush brush)
        {
            var icon = new Rectangle
            {
                Width = IconSize,
                Height = IconSize,
                OpacityMask = mask,
                ToolTip = chip.Name
            };
            if (brush != null)
            {
                icon.Fill = brush;
            }
            else
            {
                icon.SetBinding(Shape.FillProperty, new Binding { Path = new PropertyPath(TextElement.ForegroundProperty), Source = this });
            }

            return icon;
        }

        private static Brush BuildMask(string codepoint)
        {
            var image = ProviderIconConverter.BuildIcon(ProviderIconConverter.TagIconPrefix + codepoint, "#FFFFFFFF");
            if (image == null)
            {
                return null;
            }

            var mask = new ImageBrush(image) { Stretch = Stretch.Uniform };
            mask.Freeze();
            return mask;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            if (_overflow == null)
            {
                _visibleCount = 0;
                return default(Size);
            }

            var width = availableSize.Width;
            var height = Math.Min(availableSize.Height, MaxContentHeight);
            var chipCount = InternalChildren.Count - 1;
            var sizes = new Size[chipCount];
            for (var i = 0; i < chipCount; i++)
            {
                InternalChildren[i].Measure(new Size(width, double.PositiveInfinity));
                sizes[i] = InternalChildren[i].DesiredSize;
            }

            _visibleCount = TagChipLayout.VisibleCount(sizes, hidden => MeasureOverflow(hidden, width), width, height);
            var hiddenCount = chipCount - _visibleCount;
            MeasureOverflow(hiddenCount, width);
            _overflow.ToolTip = hiddenCount > 0
                ? string.Join(", ", Chips.Skip(_visibleCount).Select(chip => chip.Name))
                : null;

            return TagChipLayout.Extent(ShownChildren().Select(child => child.DesiredSize).ToList(), width);
        }

        private Size MeasureOverflow(int hiddenCount, double width)
        {
            _overflow.Text = hiddenCount > 0 ? "+" + hiddenCount.ToString(CultureInfo.CurrentCulture) : string.Empty;
            _overflow.Measure(new Size(width, double.PositiveInfinity));
            return _overflow.DesiredSize;
        }

        // The leading chips that fit, then "+N" when some do not.
        private List<UIElement> ShownChildren()
        {
            var shown = new List<UIElement>();
            for (var i = 0; i < _visibleCount; i++)
            {
                shown.Add(InternalChildren[i]);
            }

            if (_overflow != null && _visibleCount < InternalChildren.Count - 1)
            {
                shown.Add(_overflow);
            }

            return shown;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var shown = ShownChildren();
            foreach (UIElement child in InternalChildren)
            {
                if (!shown.Contains(child))
                {
                    child.Opacity = 0;
                    child.Arrange(default(Rect));
                }
            }

            var widths = shown.Select(child => Math.Min(child.DesiredSize.Width, finalSize.Width)).ToList();
            var y = 0.0;
            foreach (var line in TagChipLayout.BreakLines(widths, finalSize.Width))
            {
                var lineHeight = 0.0;
                for (var i = line.Start; i < line.Start + line.Count; i++)
                {
                    lineHeight = Math.Max(lineHeight, shown[i].DesiredSize.Height);
                }

                var x = TextAlignment == TextAlignment.Center ? (finalSize.Width - line.Width) / 2
                    : TextAlignment == TextAlignment.Right ? finalSize.Width - line.Width
                    : 0;
                for (var i = line.Start; i < line.Start + line.Count; i++)
                {
                    var child = shown[i];
                    var childHeight = child.DesiredSize.Height;
                    child.Opacity = 1;
                    child.Arrange(new Rect(x, y + (lineHeight - childHeight) / 2, widths[i], childHeight));
                    x += widths[i] + TagChipLayout.Gap;
                }

                y += lineHeight + TagChipLayout.LineGap;
            }

            return finalSize;
        }
    }
}
