using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    /// square icons (a tag without one keeps its name) and tag colors. Commas separate only
    /// neighboring names; icons are spaced apart. An icon's tooltip is its tag's name.
    /// Icons are as large as this cell's own tags let them be within its width and
    /// <see cref="MaxContentHeight"/>, up to <see cref="MaxIconScale"/> times the font size and
    /// never below it, so cells with few tags share the one ceiling size and only a crowded cell
    /// shrinks. Chips wrap onto lines aligned by <see cref="TextAlignment"/>, each chip centered in
    /// its line. Text takes its font and color from the inherited <see cref="TextElement"/> properties.
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

        public static readonly DependencyProperty MaxIconScaleProperty =
            DependencyProperty.Register(nameof(MaxIconScale), typeof(double), typeof(TagChipsPanel),
                new FrameworkPropertyMetadata(1.5, FrameworkPropertyMetadataOptions.AffectsMeasure));

        /// <summary>The largest icon size, as a multiple of the font size.</summary>
        public double MaxIconScale
        {
            get => (double)GetValue(MaxIconScaleProperty);
            set => SetValue(MaxIconScaleProperty, value);
        }

        public static readonly DependencyProperty MaxContentHeightProperty =
            DependencyProperty.Register(nameof(MaxContentHeight), typeof(double), typeof(TagChipsPanel),
                new FrameworkPropertyMetadata(double.PositiveInfinity, FrameworkPropertyMetadataOptions.AffectsMeasure));

        /// <summary>
        /// The height the tags may take; past it they are clipped, so the panel never grows its row.
        /// An auto-height grid row measures its cells without a height limit, so the grid states it.
        /// </summary>
        public double MaxContentHeight
        {
            get => (double)GetValue(MaxContentHeightProperty);
            set => SetValue(MaxContentHeightProperty, value);
        }

        public static readonly DependencyProperty TextAlignmentProperty =
            DependencyProperty.Register(nameof(TextAlignment), typeof(TextAlignment), typeof(TagChipsPanel),
                new FrameworkPropertyMetadata(TextAlignment.Left, FrameworkPropertyMetadataOptions.AffectsArrange));

        public TextAlignment TextAlignment
        {
            get => (TextAlignment)GetValue(TextAlignmentProperty);
            set => SetValue(TextAlignmentProperty, value);
        }

        public TagChipsPanel()
        {
            ClipToBounds = true;
        }

        private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
            ((TagChipsPanel)d).Rebuild();

        private void Rebuild()
        {
            InternalChildren.Clear();
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
        }

        /// <summary>
        /// The text a name chip shows: its name, with a comma when the next chip is a name too.
        /// </summary>
        private static string TextOf(IReadOnlyList<AchievementTagChip> chips, int index, AchievementTagDisplay display) =>
            index + 1 < chips.Count && IconMask(chips[index + 1], display) == null
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
            var width = availableSize.Width;
            var fontSize = TextElement.GetFontSize(this);

            // Names first: their sizes decide how much room the icons have.
            var boxes = new List<TagChipBox>(InternalChildren.Count);
            var textHeight = Math.Ceiling(fontSize);
            foreach (UIElement child in InternalChildren)
            {
                if (child is Rectangle)
                {
                    boxes.Add(TagChipBox.Icon());
                    continue;
                }

                child.Measure(new Size(width, double.PositiveInfinity));
                boxes.Add(TagChipBox.Text(Math.Ceiling(child.DesiredSize.Width)));
                textHeight = Math.Max(textHeight, child.DesiredSize.Height);
            }

            // One pixel of slack on each axis absorbs layout rounding.
            var floor = Math.Ceiling(fontSize);
            var ceiling = Math.Max(floor, Math.Floor(fontSize * MaxIconScale));
            var iconSize = TagChipLayout.LargestIconSize(
                boxes,
                textHeight,
                width - 1,
                Math.Min(availableSize.Height, MaxContentHeight) - 1,
                floor,
                ceiling) ?? ceiling;

            var widths = new double[InternalChildren.Count];
            for (var i = 0; i < InternalChildren.Count; i++)
            {
                var child = InternalChildren[i];
                if (child is Rectangle icon)
                {
                    icon.Width = iconSize;
                    icon.Height = iconSize;
                    icon.Measure(new Size(iconSize, iconSize));
                }

                widths[i] = Math.Min(child.DesiredSize.Width, width);
            }

            var extentWidth = 0.0;
            var extentHeight = 0.0;
            var lines = TagChipLayout.BreakLines(widths, width);
            for (var l = 0; l < lines.Count; l++)
            {
                extentWidth = Math.Max(extentWidth, lines[l].Width);
                extentHeight += (l > 0 ? TagChipLayout.LineGap : 0) + LineHeight(lines[l]);
            }

            // Tags too many to fit even at the smallest size are clipped rather than growing the row.
            return new Size(extentWidth, Math.Min(extentHeight, MaxContentHeight));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var widths = new double[InternalChildren.Count];
            for (var i = 0; i < InternalChildren.Count; i++)
            {
                widths[i] = Math.Min(InternalChildren[i].DesiredSize.Width, finalSize.Width);
            }

            var y = 0.0;
            foreach (var line in TagChipLayout.BreakLines(widths, finalSize.Width))
            {
                var lineHeight = LineHeight(line);
                var x = TextAlignment == TextAlignment.Center ? (finalSize.Width - line.Width) / 2
                    : TextAlignment == TextAlignment.Right ? finalSize.Width - line.Width
                    : 0;
                for (var i = line.Start; i < line.Start + line.Count; i++)
                {
                    var child = InternalChildren[i];
                    var childHeight = child.DesiredSize.Height;
                    child.Arrange(new Rect(x, y + (lineHeight - childHeight) / 2, widths[i], childHeight));
                    x += widths[i] + TagChipLayout.Gap;
                }

                y += lineHeight + TagChipLayout.LineGap;
            }

            return finalSize;
        }

        private double LineHeight(TagChipLine line)
        {
            var height = 0.0;
            for (var i = line.Start; i < line.Start + line.Count; i++)
            {
                height = Math.Max(height, InternalChildren[i].DesiredSize.Height);
            }

            return height;
        }
    }
}
