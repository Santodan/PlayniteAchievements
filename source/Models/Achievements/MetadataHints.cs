using System.Collections.Generic;

namespace PlayniteAchievements.Models.Achievements
{
    /// <summary>
    /// What a platform's scan knows about a game that helps metadata sources find it: the
    /// platform's own title for it, the trophy set titles of a collection, a region, and the
    /// Exophase platform its achievements belong to. Set by the scan on its refreshed payload and
    /// read by the metadata overlay of the same refresh; not stored.
    /// </summary>
    public sealed class MetadataHints
    {
        /// <summary>The game's title as the platform lists it, searched before the Playnite name.</summary>
        public string SearchName { get; set; }

        /// <summary>
        /// The titles of a collection's trophy sets, one page each; the collection's own name
        /// belongs to none of them, so a collection is never searched by it. Empty for a single game.
        /// </summary>
        public IReadOnlyList<string> SetTitles { get; set; } = new List<string>();

        /// <summary>A region code ("EU", "NA", "JP", "AS") that picks among a game's regional pages.</summary>
        public string RegionHint { get; set; }

        /// <summary>The Exophase platform slug of the achievements ("xbox-360" for a 360 game), or null for the platform's default.</summary>
        public string PlatformSlug { get; set; }
    }
}
