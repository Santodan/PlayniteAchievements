using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.ViewModels.Items
{
    /// <summary>One visible tag on an achievement: its name and, when set, its color.</summary>
    public sealed class AchievementTagChip
    {
        private static readonly ConcurrentDictionary<string, Brush> BrushByColor =
            new ConcurrentDictionary<string, Brush>(StringComparer.OrdinalIgnoreCase);

        private static readonly IReadOnlyList<AchievementTagChip> None = Array.Empty<AchievementTagChip>();

        public string Key { get; private set; }

        public string Name { get; private set; }

        /// <summary>The tag's color, or null for the theme's default chip color.</summary>
        public Brush Brush { get; private set; }

        public bool HasColor => Brush != null;

        /// <summary>The tag's icon glyph in Playnite's icon font, or null for none.</summary>
        public string Glyph { get; private set; }

        public bool HasGlyph => Glyph != null;

        public static IReadOnlyList<AchievementTagChip> Build(string tags, Guid? gameId)
        {
            var definitions = AchievementTagRegistry.ResolveVisible(tags, gameId);
            if (definitions.Count == 0)
            {
                return None;
            }

            return definitions
                .Select(definition => new AchievementTagChip
                {
                    Key = definition.Key,
                    Name = AchievementTagRegistry.GetName(definition),
                    Brush = ToBrush(definition.Color),
                    Glyph = AchievementTagHelper.IconGlyph(definition.Icon)
                })
                .ToList();
        }

        /// <summary>A shared frozen brush for a #RRGGBB color; null when there is no valid color.</summary>
        public static Brush ToBrush(string color)
        {
            var normalized = AchievementTagHelper.NormalizeColor(color);
            if (normalized == null)
            {
                return null;
            }

            return BrushByColor.GetOrAdd(normalized, value =>
            {
                var parsed = (Color)ColorConverter.ConvertFromString(value);
                var brush = new SolidColorBrush(parsed);
                brush.Freeze();
                return brush;
            });
        }
    }
}
