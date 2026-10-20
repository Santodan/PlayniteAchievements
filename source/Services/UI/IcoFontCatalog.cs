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

        public static IReadOnlyList<IcoFontGlyph> All => Glyphs.Value;

        /// <summary>
        /// The glyphs whose name contains every word of <paramref name="query"/>, names starting
        /// with the first word first; every glyph for a blank query.
        /// </summary>
        public static IReadOnlyList<IcoFontGlyph> Search(string query)
        {
            var words = (query ?? string.Empty)
                .Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(word => word.ToLowerInvariant())
                .ToList();
            if (words.Count == 0)
            {
                return All;
            }

            return All
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

            return glyphs;
        }
    }

    internal sealed class IcoFontGlyph
    {
        public IcoFontGlyph(string name, string code)
        {
            Name = name;
            Code = code;
            Glyph = Achievements.AchievementTagHelper.IconGlyph(code);
        }

        public string Name { get; }

        /// <summary>The codepoint as upper-case hex, as tags store it.</summary>
        public string Code { get; }

        public string Glyph { get; }
    }
}
