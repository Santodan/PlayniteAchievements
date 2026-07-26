using System.Collections.Generic;

namespace PlayniteAchievements.Providers.Exophase
{
    /// <summary>
    /// Ordered catalog of the Exophase friend platform tokens shown in the settings friends grid,
    /// paired with the localization key used to label each one. The tokens match the per-friend
    /// values stored in <see cref="ExophaseFriendSettings.SelectedPlatforms"/>.
    /// </summary>
    internal static class ExophaseFriendPlatformCatalog
    {
        public readonly struct Entry
        {
            public Entry(string token, string labelKey)
            {
                Token = token;
                LabelKey = labelKey;
            }

            public string Token { get; }

            public string LabelKey { get; }
        }

        public static IReadOnlyList<Entry> Entries { get; } = new List<Entry>
        {
            new Entry("psn", "LOCPlayAch_Provider_PSN"),
            new Entry("xbox", "LOCPlayAch_Provider_Xbox"),
            new Entry("gog", "LOCPlayAch_Provider_GOG"),
            new Entry("epic", "LOCPlayAch_Provider_Epic"),
            new Entry("blizzard", "LOCPlayAch_Provider_BattleNet"),
            new Entry("origin", "LOCPlayAch_Provider_EA"),
            new Entry("android", "LOCPlayAch_Provider_GooglePlay"),
            new Entry("apple", "LOCPlayAch_Provider_Apple"),
            new Entry("ubisoft", "LOCPlayAch_Provider_Ubisoft"),
            new Entry("retro", "LOCPlayAch_Provider_RetroAchievements"),
        };
    }
}
