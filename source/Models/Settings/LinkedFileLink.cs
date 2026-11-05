using Newtonsoft.Json;
using System;

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

        [JsonIgnore]
        public bool HasFilePath => !string.IsNullOrWhiteSpace(FilePath);

        public LinkedFileLink Clone()
        {
            return new LinkedFileLink
            {
                FilePath = FilePath,
                Schema = Schema?.Clone(),
                LastReadUtc = LastReadUtc,
                LastReadStatus = LastReadStatus
            };
        }
    }
}
