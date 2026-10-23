using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// The glyph names of Playnite's icon font, for finding an icon by name. The font itself
    /// carries no names, so they come from the embedded IcoFont list; the glyphs render from the
    /// font Playnite already ships.
    /// </summary>
    internal static class IcoFontCatalog
    {
        private const string ResourceName = "PlayniteAchievements.Resources.IcoFontNames.txt";

        private static readonly Lazy<IReadOnlyList<IcoFontGlyph>> Glyphs =
            new Lazy<IReadOnlyList<IcoFontGlyph>>(Load);

        // IcoFont numbers its glyphs category by category, alphabetical within each, so a category
        // is the run of codepoints from its first one up to the next category's. The names are
        // IcoFont's own, like the glyph names; each category's icon is one of its own glyphs.
        private static readonly IcoFontCategory[] CategoryStarts =
        {
            new IcoFontCategory(0xE800, "Abstract", "E814"),
            new IcoFontCategory(0xE829, "Animal", "E83D"),
            new IcoFontCategory(0xE896, "Brand", "E8A7"),
            new IcoFontCategory(0xE961, "Business", "E966"),
            new IcoFontCategory(0xE978, "Chart", "E983"),
            new IcoFontCategory(0xE985, "Construction", "E9A0"),
            new IcoFontCategory(0xE9C5, "Currency", "E9D8"),
            new IcoFontCategory(0xEA29, "Device", "EA30"),
            new IcoFontCategory(0xEA5B, "Directional", "EA5D"),
            new IcoFontCategory(0xEACC, "Education", "EADC"),
            new IcoFontCategory(0xEAF4, "Emoji", "EB02"),
            new IcoFontCategory(0xEB08, "File Type", "EB0E"),
            new IcoFontCategory(0xEB31, "Fitness", "EB3A"),
            new IcoFontCategory(0xEB3B, "Food", "EB69"),
            new IcoFontCategory(0xEB9D, "Kids & Baby", "EBA1"),
            new IcoFontCategory(0xEBB0, "Law", "EBC7"),
            new IcoFontCategory(0xEBD7, "Math", "EBDF"),
            new IcoFontCategory(0xEC06, "Medical", "EC1D"),
            new IcoFontCategory(0xEC3E, "Mobile UI", "EC8A"),
            new IcoFontCategory(0xEC99, "Multimedia", "ECA3"),
            new IcoFontCategory(0xECBC, "Payment", "ECC9"),
            new IcoFontCategory(0xECEB, "Person", "ED05"),
            new IcoFontCategory(0xED11, "Search", "ED1B"),
            new IcoFontCategory(0xED1C, "Social", "ED37"),
            new IcoFontCategory(0xED8D, "Sport", "EDA0"),
            new IcoFontCategory(0xEDDF, "Text Editor", "EDE3"),
            new IcoFontCategory(0xEE0C, "Transport", "EE1F"),
            new IcoFontCategory(0xEE41, "Travel", "EE44"),
            new IcoFontCategory(0xEE4F, "Weather", "EE81"),
            new IcoFontCategory(0xEEA0, "Web Application", "EF3E")
        };

        /// <summary>Every glyph, grouped by category in IcoFont's order and by name within each.</summary>
        public static IReadOnlyList<IcoFontGlyph> All => Glyphs.Value;

        /// <summary>The categories, in IcoFont's order.</summary>
        public static IReadOnlyList<IcoFontCategory> Categories => CategoryStarts;

        /// <summary>The category of the glyph at <paramref name="code"/>; null for a code before the first.</summary>
        public static string CategoryOf(string code)
        {
            if (!int.TryParse(code, System.Globalization.NumberStyles.HexNumber, null, out var codepoint))
            {
                return null;
            }

            string category = null;
            foreach (var start in CategoryStarts)
            {
                if (codepoint < start.Start)
                {
                    break;
                }

                category = start.Name;
            }

            return category;
        }

        /// <summary>
        /// The glyphs of <paramref name="category"/> (every category when null) whose name contains
        /// every word of <paramref name="query"/>, names starting with the first word first; all of
        /// the category's glyphs, in <see cref="All"/>'s order, for a blank query.
        /// </summary>
        public static IReadOnlyList<IcoFontGlyph> Search(string query, string category = null)
        {
            var pool = category == null
                ? All
                : All.Where(glyph => glyph.Category == category).ToList();
            var words = (query ?? string.Empty)
                .Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(word => word.ToLowerInvariant())
                .ToList();
            if (words.Count == 0)
            {
                return pool;
            }

            return pool
                .Where(glyph => words.All(word => glyph.Name.IndexOf(word, StringComparison.Ordinal) >= 0))
                .OrderBy(glyph => glyph.Name.StartsWith(words[0], StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(glyph => glyph.Name, StringComparer.Ordinal)
                .ToList();
        }

        public static string NameOf(string code)
        {
            return All.FirstOrDefault(glyph => string.Equals(glyph.Code, code, StringComparison.OrdinalIgnoreCase))?.Name;
        }

        private static IReadOnlyList<IcoFontGlyph> Load()
        {
            var glyphs = new List<IcoFontGlyph>();
            try
            {
                using (var stream = typeof(IcoFontCatalog).Assembly.GetManifestResourceStream(ResourceName))
                {
                    if (stream == null)
                    {
                        return glyphs;
                    }

                    using (var reader = new StreamReader(stream))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            var parts = line.Split(' ');
                            if (line.StartsWith("#", StringComparison.Ordinal) || parts.Length != 2)
                            {
                                continue;
                            }

                            var code = Achievements.AchievementTagHelper.NormalizeIcon(parts[1]);
                            if (code != null)
                            {
                                glyphs.Add(new IcoFontGlyph(parts[0], code));
                            }
                        }
                    }
                }
            }
            catch
            {
                // A missing list only costs the search; the picker still lists nothing rather than failing.
            }

            // The codepoint order is the category order, alphabetical within each category.
            return glyphs
                .OrderBy(glyph => glyph.Code, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    internal sealed class IcoFontGlyph
    {
        public IcoFontGlyph(string name, string code)
        {
            Name = name;
            Code = code;
            Glyph = Achievements.AchievementTagHelper.IconGlyph(code);
            Category = IcoFontCatalog.CategoryOf(code);
        }

        public string Name { get; }

        /// <summary>The IcoFont category the glyph belongs to.</summary>
        public string Category { get; }

        /// <summary>The codepoint as upper-case hex, as tags store it.</summary>
        public string Code { get; }

        public string Glyph { get; }
    }

    /// <summary>One IcoFont category: where its codepoints start, its name and its icon's codepoint.</summary>
    internal sealed class IcoFontCategory
    {
        public IcoFontCategory(int start, string name, string icon)
        {
            Start = start;
            Name = name;
            Icon = icon;
        }

        public int Start { get; }

        public string Name { get; }

        public string Icon { get; }
    }
}
