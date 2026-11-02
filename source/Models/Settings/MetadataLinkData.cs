using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Which game a metadata source supplies data from for this game: a PlayStation trophy set id,
    /// a Steam app id. Stored with the game's custom data so it travels with exports and the
    /// Workshop; the borrowed data itself is fetched again on each refresh.
    /// </summary>
    public sealed class MetadataLinkData
    {
        /// <summary>The source's game id; null on a link the user set to "no match".</summary>
        public string Id { get; set; }

        public string Label { get; set; }

        /// <summary>Set by the user rather than matched automatically; automatic matching never replaces it.</summary>
        public bool IsUserSet { get; set; }

        /// <summary>
        /// Achievement pairings the user set, by the game's ApiName: the source row's ApiName, or an
        /// empty string for "never borrow for this achievement". Matching fills in the rest. They
        /// belong to <see cref="Id"/> and are dropped when the link changes.
        /// </summary>
        public Dictionary<string, string> Pairings { get; set; }

        public MetadataLinkData Clone()
        {
            return new MetadataLinkData
            {
                Id = Id,
                Label = Label,
                IsUserSet = IsUserSet,
                Pairings = Pairings == null ? null : new Dictionary<string, string>(Pairings, StringComparer.OrdinalIgnoreCase)
            };
        }

        internal static Dictionary<string, MetadataLinkData> CloneMap(Dictionary<string, MetadataLinkData> links)
        {
            return links == null
                ? null
                : links.Where(pair => pair.Value != null)
                    .ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        }
    }
}
