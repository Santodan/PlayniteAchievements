using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services.Metadata
{
    /// <summary>The pages a name search chose: one, or one per game of a collection, in order.</summary>
    internal sealed class MetadataPageChoice
    {
        public MetadataPageChoice(
            IReadOnlyList<string> ids,
            int matched,
            IReadOnlyList<(string Category, string Title)> names = null,
            IReadOnlyList<string> found = null)
        {
            Ids = ids;
            Matched = matched;
            Names = names ?? Array.Empty<(string, string)>();
            Found = found ?? Array.Empty<string>();
        }

        /// <summary>How each chosen page was found, in order ("whole game: search #1 for name 'Hades'").</summary>
        public IReadOnlyList<string> Found { get; }

        /// <summary>The lookup's trace: how each page was found, and what it cost. Set by the search.</summary>
        public string Trace { get; internal set; }

        public IReadOnlyList<string> Ids { get; }

        /// <summary>How many of the game's achievements the chosen pages matched.</summary>
        public int Matched { get; }

        /// <summary>
        /// The game's categories large enough to look for on their own that the chosen pages match
        /// none of: a game or DLC the source may list on a page the search did not find.
        /// </summary>
        public IReadOnlyList<string> Uncovered { get; internal set; } = Array.Empty<string>();

        /// <summary>
        /// Each chosen page's title on the source, with the category it was found for (null for
        /// the whole game). Stored as the link's label, so other sources search these names too.
        /// </summary>
        public IReadOnlyList<(string Category, string Title)> Names { get; }

        public string Label => MetadataFoundNames.Format(Names);
    }
}
