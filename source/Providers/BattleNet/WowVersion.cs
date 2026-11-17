using System;

namespace PlayniteAchievements.Providers.BattleNet
{
    /// <summary>
    /// One World of Warcraft game version and where its data lives. All versions share the retail
    /// region and the account-character setting; each keeps its own realm and character.
    /// </summary>
    internal sealed class WowVersion
    {
        public static readonly WowVersion Retail = new WowVersion(
            BattleNetGameTitle.Wow,
            namespacePrefix: string.Empty,
            appIdSeed: "WoW",
            logTag: "[BattleNet/WoW]",
            dataForAzerothPath: "retail",
            minimumDataForAzerothRarityEntries: 1000,
            realmSlug: s => s.WowRealmSlug,
            character: s => s.WowCharacter);

        /// <summary>The progression Classic realms, served under the "classic-" namespaces.</summary>
        public static readonly WowVersion Classic = new WowVersion(
            BattleNetGameTitle.WowClassic,
            namespacePrefix: "classic-",
            appIdSeed: "WoWClassic",
            logTag: "[BattleNet/WoWClassic]",
            dataForAzerothPath: null,
            minimumDataForAzerothRarityEntries: 0,
            realmSlug: s => s.WowClassicRealmSlug,
            character: s => s.WowClassicCharacter);

        /// <summary>
        /// Forever has no named realms, so there is no configured character; its characters come
        /// from the signed-in account, addressed through each character's profile link.
        /// </summary>
        public static readonly WowVersion Forever = new WowVersion(
            BattleNetGameTitle.WowForever,
            namespacePrefix: "forever-",
            appIdSeed: "WoWForever",
            logTag: "[BattleNet/WoWForever]",
            dataForAzerothPath: "forever",
            minimumDataForAzerothRarityEntries: 25,
            realmSlug: s => null,
            character: s => null);

        private readonly Func<BattleNetSettings, string> _realmSlug;
        private readonly Func<BattleNetSettings, string> _character;

        private WowVersion(
            BattleNetGameTitle title,
            string namespacePrefix,
            string appIdSeed,
            string logTag,
            string dataForAzerothPath,
            int minimumDataForAzerothRarityEntries,
            Func<BattleNetSettings, string> realmSlug,
            Func<BattleNetSettings, string> character)
        {
            Title = title;
            NamespacePrefix = namespacePrefix;
            AppIdSeed = appIdSeed;
            LogTag = logTag;
            DataForAzerothPath = dataForAzerothPath;
            MinimumDataForAzerothRarityEntries = minimumDataForAzerothRarityEntries;
            _realmSlug = realmSlug;
            _character = character;
        }

        public BattleNetGameTitle Title { get; }

        /// <summary>Inserted between "static-"/"profile-" and the region in API namespaces.</summary>
        public string NamespacePrefix { get; }

        public string AppIdSeed { get; }

        public string LogTag { get; }

        /// <summary>
        /// The version's folder on dataforazeroth.com, as in "{path}/dynamic/index.json", or null
        /// when the site publishes no data for it.
        /// </summary>
        public string DataForAzerothPath { get; }

        /// <summary>
        /// The smallest Data for Azeroth rarity map accepted as complete for this version. The
        /// version's achievement catalog sets the scale: retail lists thousands, Forever under 200.
        /// </summary>
        public int MinimumDataForAzerothRarityEntries { get; }

        /// <summary>
        /// Retail builds its catalog from the public worldofwarcraft.com character pages; the other
        /// versions build theirs from the official API only.
        /// </summary>
        public bool IsRetail => NamespacePrefix.Length == 0;

        public string RealmSlug(BattleNetSettings settings) => settings == null ? null : _realmSlug(settings);

        public string Character(BattleNetSettings settings) => settings == null ? null : _character(settings);

        public static WowVersion ForTitle(BattleNetGameTitle title)
        {
            switch (title)
            {
                case BattleNetGameTitle.Wow:
                    return Retail;
                case BattleNetGameTitle.WowClassic:
                    return Classic;
                case BattleNetGameTitle.WowForever:
                    return Forever;
                default:
                    return null;
            }
        }
    }
}
