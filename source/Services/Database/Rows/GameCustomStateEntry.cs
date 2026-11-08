using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Database.Rows
{
    /// <summary>
    /// One game's row in the game-level custom state mirror: the flags and per-category kinds the
    /// resolved views need from the custom-data store.
    /// </summary>
    internal sealed class GameCustomStateEntry
    {
        /// <summary>True once the user edited capstones; the stored set then replaces the provider's flags.</summary>
        public bool CapstonesMaterialized { get; set; }

        /// <summary>A manual link records unlock times itself, so unlock-time overrides do not apply.</summary>
        public bool HasManualLink { get; set; }

        public bool ExcludedFromSummaries { get; set; }

        public bool ExcludedFromRefreshes { get; set; }

        public bool UseSeparateLockedIcons { get; set; }

        /// <summary>Kind per category path; "None" says the category has none.</summary>
        public Dictionary<string, string> CategoryKinds { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public bool IsEmpty =>
            !CapstonesMaterialized && !HasManualLink && !ExcludedFromSummaries && !ExcludedFromRefreshes &&
            !UseSeparateLockedIcons && (CategoryKinds == null || CategoryKinds.Count == 0);

        public string ToSignature() =>
            string.Join(
                "\n",
                CapstonesMaterialized ? "1" : "0",
                HasManualLink ? "1" : "0",
                ExcludedFromSummaries ? "1" : "0",
                ExcludedFromRefreshes ? "1" : "0",
                UseSeparateLockedIcons ? "1" : "0",
                string.Join("|", (CategoryKinds ?? new Dictionary<string, string>())
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => pair.Key + "=" + pair.Value)));
    }
}
