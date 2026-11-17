using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// A game's linked file: the file on this machine that supplies unlock status and the mapping
    /// that reads it. The mapping travels in the portable package; the path and the read status
    /// are local state and never leave this machine.
    /// </summary>
    public sealed class LinkedFileLink
    {
        public string FilePath { get; set; }

        public LinkedFileSchema Schema { get; set; }

        public DateTime? LastReadUtc { get; set; }

        /// <summary>The <c>LinkedFileReadStatus</c> name of the last read, or null before one.</summary>
        public string LastReadStatus { get; set; }

        /// <summary>
        /// Rows the user paired by hand, by row key (see <c>LinkedFileMatcher.RowKey</c>): the
        /// achievement's API name, or "" for a row that pairs with nothing. Rows not listed are
        /// matched automatically. Null when there are none.
        /// </summary>
        public Dictionary<string, string> Pairings { get; set; }

        [JsonIgnore]
        public bool HasFilePath => !string.IsNullOrWhiteSpace(FilePath);

        public LinkedFileLink Clone()
        {
            return new LinkedFileLink
            {
                FilePath = FilePath,
                Schema = Schema?.Clone(),
                LastReadUtc = LastReadUtc,
                LastReadStatus = LastReadStatus,
                Pairings = Pairings == null ? null : new Dictionary<string, string>(Pairings, StringComparer.Ordinal)
            };
        }
    }
}
