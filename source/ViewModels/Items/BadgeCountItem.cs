using System.Collections.Generic;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.ViewModels.Items
{
    /// <summary>
    /// A badge icon with a formatted count beside it: a profile medal, or a points card's rarity
    /// badge under its bar. A provider badge draws the platform's icon in its color instead of an
    /// image resource.
    /// </summary>
    public sealed class BadgeCountItem
    {
        public BadgeCountItem(string iconKey, string countText)
        {
            IconKey = iconKey;
            CountText = countText;
        }

        private BadgeCountItem(string providerIconKey, string providerColorHex, string countText, string toolTip)
        {
            ProviderIconKey = providerIconKey;
            ProviderColorHex = providerColorHex;
            CountText = countText;
            ToolTip = toolTip;
            IsProvider = true;
        }

        public string IconKey { get; }

        public string CountText { get; }

        /// <summary>True for a provider badge, drawn from <see cref="ProviderIconKey"/> and <see cref="ProviderColorHex"/>.</summary>
        public bool IsProvider { get; }

        public string ProviderIconKey { get; }

        public string ProviderColorHex { get; }

        public string ToolTip { get; }

        /// <summary>A provider badge for <paramref name="count"/>, rounded to a whole number.</summary>
        public static BadgeCountItem ForProvider(string iconKey, string colorHex, double count, string toolTip)
        {
            return new BadgeCountItem(
                iconKey,
                colorHex,
                count.ToString("N0", FormattingCulture.Current),
                toolTip);
        }

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
