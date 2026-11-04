using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.ViewModels.Tags
{
    /// <summary>One visible tag as drawn: its name, its icon codepoint and its color brush.</summary>
    public sealed class AchievementTagChip
    {
        public AchievementTagChip(string name, string icon, Brush brush)
        {
            Name = name;
            Icon = icon;
            Brush = brush;
        }

        public string Name { get; }

        /// <summary>The icon font codepoint ("EF12"), or null when the tag has no icon.</summary>
        public string Icon { get; }

        /// <summary>The tag's color, or null when it has none.</summary>
        public Brush Brush { get; }
    }

    /// <summary>
    /// Brushes and glyphs for tags, shared by the tag editors and the achievement grids. Every
    /// lookup reads through <see cref="AchievementTagRegistry"/>, so tags come back in tag list
    /// order with renames and recolors applied.
    /// </summary>
    internal static class AchievementTagPresentation
    {
        private static readonly ConcurrentDictionary<string, Brush> BrushByColor =
            new ConcurrentDictionary<string, Brush>(StringComparer.OrdinalIgnoreCase);

        /// <summary>One frozen brush per color; null when <paramref name="color"/> is not a color.</summary>
        public static Brush ToBrush(string color)
        {
            var normalized = AchievementTagHelper.NormalizeColor(color);
            if (normalized == null)
            {
                return null;
            }

            return BrushByColor.GetOrAdd(normalized, value =>
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
                brush.Freeze();
                return brush;
            });
        }

        /// <summary>
        /// The visible tags of <paramref name="tags"/>, in tag list order; hidden ones too when
        /// <paramref name="includeHidden"/>, as the editor shows them.
        /// </summary>
        public static IReadOnlyList<AchievementTagChip> Resolve(string tags, Guid? gameId, bool includeHidden = false)
        {
            var definitions = AchievementTagRegistry.ResolveVisible(tags, gameId, includeHidden);
            if (definitions.Count == 0)
            {
                return Array.Empty<AchievementTagChip>();
            }

            return definitions
                .Select(definition => new AchievementTagChip(
                    AchievementTagRegistry.GetName(definition),
                    AchievementTagHelper.NormalizeIcon(definition.Icon),
                    ToBrush(definition.Color)))
                .ToList();
        }

        /// <summary>
        /// The brush of the first visible tag of <paramref name="tags"/> that has a color, among
        /// the tags <paramref name="include"/> accepts (all of them when null). Null when none does.
        /// </summary>
        public static Brush FirstColoredBrush(string tags, Guid? gameId, Func<string, bool> include = null)
        {
            foreach (var definition in AchievementTagRegistry.ResolveVisible(tags, gameId))
            {
                if (include != null && !include(definition.Key))
                {
                    continue;
                }

                var brush = ToBrush(definition.Color);
                if (brush != null)
                {
                    return brush;
                }
            }

            return null;
        }
    }
}
