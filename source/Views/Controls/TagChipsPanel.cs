using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
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
    /// Text takes its font and color from the inherited <see cref="TextElement"/> properties.
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

        /// <summary>
        /// Raised, bubbling, when a panel's tags change, so the grid hosting it can refit its
        /// column's icon size to a row that gained or lost tags.
        /// </summary>
        public static readonly RoutedEvent ChipsChangedEvent =
            EventManager.RegisterRoutedEvent("ChipsChanged", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(TagChipsPanel));

        private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var panel = (TagChipsPanel)d;
            panel.Rebuild();
            if (e.Property != ChipsProperty)
            {
                return;
            }

            // Only for the same row's tags changing: a row realized or recycled onto another item
            // brings that item's tags, which the grid's last fit already counted.
            if (e.OldValue != null && ReferenceEquals(panel.DataContext, panel._chipsOwner))
            {
                panel.RaiseEvent(new RoutedEventArgs(ChipsChangedEvent, panel));
            }

            panel._chipsOwner = panel.DataContext;
        }

        private object _chipsOwner;

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

        /// <summary>
        /// The one icon size for a column of tag cells <paramref name="width"/> by
        /// <paramref name="height"/>: the largest at which every row's chips fit, never below the
        /// body text size. Rows sharing the same chips are measured once. The body font comes from
        /// <paramref name="fontSource"/>'s resources.
        /// </summary>
        public static double FitIconSize(
            IEnumerable<IReadOnlyList<AchievementTagChip>> rows,
            AchievementTagDisplay display,
            double width,
            double height,
            FrameworkElement fontSource)
        {
            var family = fontSource.TryFindResource("PlayAch.FontFamily.Body") as FontFamily ?? SystemFonts.MessageFontFamily;
            var fontSize = fontSource.TryFindResource("PlayAch.FontSize.Body") is double size && size > 0 ? size : SystemFonts.MessageFontSize;
            var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var pixelsPerDip = VisualTreeHelper.GetDpi(fontSource).PixelsPerDip;
            var textHeight = Math.Ceiling(family.LineSpacing * fontSize);
            var floor = Math.Ceiling(fontSize);

            // One pixel of slack on each axis absorbs layout rounding.
            width -= 1;
            height -= 1;
            var best = Math.Floor(Math.Min(width, height));

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var signature = new StringBuilder();
            var boxes = new List<TagChipBox>();
            foreach (var chips in rows)
            {
                if (chips == null || chips.Count == 0)
                {
                    continue;
                }

                signature.Clear();
                boxes.Clear();
                for (var i = 0; i < chips.Count; i++)
                {
                    if (IsIcon(chips[i], display))
                    {
                        signature.Append("\u0001I");
                        boxes.Add(TagChipBox.Icon());
                    }
                    else
                    {
                        var text = TextOf(chips, i, display);
                        signature.Append("\u0001T").Append(text);
                        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                            typeface, fontSize, Brushes.Black, pixelsPerDip);
                        boxes.Add(TagChipBox.Text(Math.Ceiling(formatted.WidthIncludingTrailingWhitespace) + 1));
                    }
                }

                if (!seen.Add(signature.ToString()))
                {
                    continue;
                }

                var fit = TagChipLayout.LargestIconSize(boxes, textHeight, width, height, floor);
                if (fit.HasValue)
                {
                    best = Math.Min(best, fit.Value);
                }
            }

            return Math.Max(floor, best);
        }

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
            var widths = new double[InternalChildren.Count];
            for (var i = 0; i < InternalChildren.Count; i++)
            {
                var child = InternalChildren[i];
                child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
                widths[i] = Math.Min(child.DesiredSize.Width, availableSize.Width);
            }

            var width = 0.0;
            var height = 0.0;
            var lines = TagChipLayout.BreakLines(widths, availableSize.Width);
            for (var l = 0; l < lines.Count; l++)
            {
                width = Math.Max(width, lines[l].Width);
                height += (l > 0 ? TagChipLayout.LineGap : 0) + LineHeight(lines[l]);
            }

            return new Size(width, height);
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
