using System.Collections.Generic;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.ViewModels.Items
{
    /// <summary>
    /// A badge icon with a formatted count beside it: a profile medal, or a points card's rarity
    /// badge under its bar.
    /// </summary>
    public sealed class BadgeCountItem
    {
        public BadgeCountItem(string iconKey, string countText)
        {
            IconKey = iconKey;
            CountText = countText;
        }

        public string IconKey { get; }

        public string CountText { get; }

        /// <summary>Rarity badges with their unlock counts, rarest first, skipping tiers with none.</summary>
        public static List<BadgeCountItem> BuildRarity(int ultraRare, int rare, int uncommon, int common)
        {
            var items = new List<BadgeCountItem>();
            Add(items, "BadgeRarityUltraRare", ultraRare);
            Add(items, "BadgeRarityRare", rare);
            Add(items, "BadgeRarityUncommon", uncommon);
            Add(items, "BadgeRarityCommon", common);
            return items;
        }

        /// <summary>Adds a badge for <paramref name="count"/>, or nothing when it is zero.</summary>
        public static void Add(List<BadgeCountItem> items, string iconKey, int count)
        {
            if (count > 0)
            {
                items.Add(new BadgeCountItem(iconKey, count.ToString("N0", FormattingCulture.Current)));
            }
        }
    }
}
