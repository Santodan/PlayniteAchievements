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

        // The picker's groups, in the order it lists them, each with one of its own glyphs as its
        // icon. Each folds together related IcoFont categories.
        private static readonly IcoFontCategory[] Groups =
        {
            new IcoFontCategory("Interface", "EC8A"),
            new IcoFontCategory("Arrows", "EA5D"),
            new IcoFontCategory("Fantasy", "E806"),
            new IcoFontCategory("People", "ED05"),
            new IcoFontCategory("Animals & Nature", "E83D"),
            new IcoFontCategory("Food", "EB69"),
            new IcoFontCategory("Sport & Health", "EDA0"),
            new IcoFontCategory("Travel", "EE1F"),
            new IcoFontCategory("Devices & Media", "EA30"),
            new IcoFontCategory("Office & School", "EB0E"),
            new IcoFontCategory("Work & Money", "E966"),
            new IcoFontCategory("Logos", "E8A7")
        };

        // IcoFont numbers its glyphs category by category, alphabetical within each, so a category
        // is the run of codepoints from its first one up to the next category's. Each entry is
        // where one IcoFont category starts (named in the comment) and the group it falls in.
        private static readonly KeyValuePair<int, string>[] CategoryStarts =
        {
            new KeyValuePair<int, string>(0xE800, "Fantasy"),           // Abstract
            new KeyValuePair<int, string>(0xE829, "Animals & Nature"),  // Animal
            new KeyValuePair<int, string>(0xE896, "Logos"),             // Brand
            new KeyValuePair<int, string>(0xE961, "Work & Money"),      // Business
            new KeyValuePair<int, string>(0xE978, "Work & Money"),      // Chart
            new KeyValuePair<int, string>(0xE985, "Work & Money"),      // Construction
            new KeyValuePair<int, string>(0xE9C5, "Work & Money"),      // Currency
            new KeyValuePair<int, string>(0xEA29, "Devices & Media"),   // Device
            new KeyValuePair<int, string>(0xEA5B, "Arrows"),            // Directional
            new KeyValuePair<int, string>(0xEACC, "Office & School"),   // Education
            new KeyValuePair<int, string>(0xEAF4, "People"),            // Emoji
            new KeyValuePair<int, string>(0xEB08, "Office & School"),   // File Type
            new KeyValuePair<int, string>(0xEB31, "Sport & Health"),    // Fitness
            new KeyValuePair<int, string>(0xEB3B, "Food"),              // Food
            new KeyValuePair<int, string>(0xEB9D, "People"),            // Kids & Baby
            new KeyValuePair<int, string>(0xEBB0, "Work & Money"),      // Law
            new KeyValuePair<int, string>(0xEBD7, "Office & School"),   // Math
            new KeyValuePair<int, string>(0xEC06, "Sport & Health"),    // Medical
            new KeyValuePair<int, string>(0xEC3E, "Interface"),         // Mobile UI
            new KeyValuePair<int, string>(0xEC99, "Devices & Media"),   // Multimedia
            new KeyValuePair<int, string>(0xECBC, "Logos"),             // Payment
            new KeyValuePair<int, string>(0xECEB, "People"),            // Person
            new KeyValuePair<int, string>(0xED11, "Interface"),         // Search
            new KeyValuePair<int, string>(0xED1C, "Logos"),             // Social
            new KeyValuePair<int, string>(0xED8D, "Sport & Health"),    // Sport
            new KeyValuePair<int, string>(0xEDDF, "Office & School"),   // Text Editor
            new KeyValuePair<int, string>(0xEE0C, "Travel"),            // Transport
            new KeyValuePair<int, string>(0xEE41, "Travel"),            // Travel
            new KeyValuePair<int, string>(0xEE4F, "Animals & Nature"),  // Weather
            new KeyValuePair<int, string>(0xEEA0, "Interface")          // Web Application
        };

        /// <summary>Every glyph, grouped in <see cref="Categories"/>' order and by codepoint within each.</summary>
        public static IReadOnlyList<IcoFontGlyph> All => Glyphs.Value;

        /// <summary>The picker's groups, in the order it lists them.</summary>
        public static IReadOnlyList<IcoFontCategory> Categories => Groups;

        /// <summary>The group of the glyph at <paramref name="code"/>; null for a code before the first.</summary>
        public static string CategoryOf(string code)
        {
            if (!int.TryParse(code, System.Globalization.NumberStyles.HexNumber, null, out var codepoint))
            {
                return null;
            }

            string category = null;
            foreach (var start in CategoryStarts)
            {
                if (codepoint < start.Key)
                {
                    break;
                }

                category = start.Value;
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

            // By group, then by codepoint, which keeps each IcoFont category together and
            // alphabetical within it.
            return glyphs
                .OrderBy(glyph => Array.FindIndex(Groups, group => group.Name == glyph.Category))
                .ThenBy(glyph => glyph.Code, StringComparer.OrdinalIgnoreCase)
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

    /// <summary>One group of the picker: its name and its icon's codepoint.</summary>
    internal sealed class IcoFontCategory
    {
        public IcoFontCategory(string name, string icon)
        {
            Name = name;
            Icon = icon;
        }

        public string Name { get; }

        public string Icon { get; }
    }
}
