using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.ViewModels.Tags;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Fills a TextBlock with an achievement's tags, comma separated, drawn as the grid's
    /// <see cref="AchievementTagDisplay"/> asks: names, icons (a tag without one keeps its name)
    /// and tag colors. An icon's tooltip is its tag's name. One run per tag keeps the cell a
    /// single TextBlock, so it wraps and aligns like plain text.
    /// </summary>
    public static class TagInlines
    {
        private const string Separator = ", ";

        public static readonly DependencyProperty ChipsProperty =
            DependencyProperty.RegisterAttached(
                "Chips",
                typeof(IReadOnlyList<AchievementTagChip>),
                typeof(TagInlines),
                new PropertyMetadata(null, OnChanged));

        public static IReadOnlyList<AchievementTagChip> GetChips(DependencyObject element) =>
            (IReadOnlyList<AchievementTagChip>)element.GetValue(ChipsProperty);

        public static void SetChips(DependencyObject element, IReadOnlyList<AchievementTagChip> value) =>
            element.SetValue(ChipsProperty, value);

        public static readonly DependencyProperty DisplayProperty =
            DependencyProperty.RegisterAttached(
                "Display",
                typeof(AchievementTagDisplay),
                typeof(TagInlines),
                new PropertyMetadata(AchievementTagDisplay.None, OnChanged));

        public static AchievementTagDisplay GetDisplay(DependencyObject element) =>
            (AchievementTagDisplay)element.GetValue(DisplayProperty);

        public static void SetDisplay(DependencyObject element, AchievementTagDisplay value) =>
            element.SetValue(DisplayProperty, value);

        private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TextBlock textBlock)
            {
                Apply(textBlock, GetChips(textBlock), GetDisplay(textBlock));
            }
        }

        private static void Apply(TextBlock textBlock, IReadOnlyList<AchievementTagChip> chips, AchievementTagDisplay display)
        {
            var inlines = textBlock.Inlines;
            inlines.Clear();
            if (chips == null || chips.Count == 0)
            {
                return;
            }

            var icons = (display & AchievementTagDisplay.Icons) != 0;
            var colors = (display & AchievementTagDisplay.Colors) != 0;
            for (var i = 0; i < chips.Count; i++)
            {
                if (i > 0)
                {
                    inlines.Add(new Run(Separator));
                }

                var chip = chips[i];
                Run run;
                if (icons && chip.Glyph != null)
                {
                    run = new Run(chip.Glyph) { ToolTip = chip.Name };
                    run.SetResourceReference(TextElement.FontFamilyProperty, "PlayAch.FontFamily.Icon");
                }
                else
                {
                    run = new Run(chip.Name);
                }

                if (colors && chip.Brush != null)
                {
                    run.Foreground = chip.Brush;
                }

                inlines.Add(run);
            }
        }
    }
}
