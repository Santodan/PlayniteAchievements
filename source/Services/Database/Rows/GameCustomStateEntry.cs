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

        /// <summary>The user's settings per category path.</summary>
        public Dictionary<string, CategoryState> Categories { get; set; } =
            new Dictionary<string, CategoryState>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The entry for <paramref name="category"/>, created when missing.</summary>
        public CategoryState Category(string category)
        {
            if (!Categories.TryGetValue(category, out var state))
            {
                Categories[category] = state = new CategoryState();
            }

            return state;
        }

        public bool IsEmpty =>
            !CapstonesMaterialized && !HasManualLink && !ExcludedFromSummaries && !ExcludedFromRefreshes &&
            !UseSeparateLockedIcons && (Categories == null || Categories.Count == 0);

        public string ToSignature() =>
            string.Join(
                "\n",
                CapstonesMaterialized ? "1" : "0",
                HasManualLink ? "1" : "0",
                ExcludedFromSummaries ? "1" : "0",
                ExcludedFromRefreshes ? "1" : "0",
                UseSeparateLockedIcons ? "1" : "0",
                string.Join("|", (Categories ?? new Dictionary<string, CategoryState>())
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => pair.Key + "=" + pair.Value?.Kind + "," + pair.Value?.SortIndex + "," + pair.Value?.ImagePath)));
    }

    /// <summary>One category's settings: its kind ("None" for none), position in the custom category order and custom art.</summary>
    internal sealed class CategoryState
    {
        public string Kind { get; set; }

        public int? SortIndex { get; set; }

        /// <summary>The custom art to show, relative to the icon cache like the achievement icons.</summary>
        public string ImagePath { get; set; }
    }
}
