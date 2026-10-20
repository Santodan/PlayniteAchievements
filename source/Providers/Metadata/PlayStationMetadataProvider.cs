using Playnite.SDK;
using PlayniteAchievements.Models;
using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Providers.Metadata
{
    /// <summary>
    /// PlayStation trophy icons, trophy types and platinum art for games another provider
    /// services, read from Exophase's public trophy pages: original-size icons (512px for PS5),
    /// preferring the PS5 page.
    /// </summary>
    internal sealed class PlayStationMetadataProvider : ExophasePageMetadataProvider
    {
        public const string MetadataKey = "PlayStation";

        private static readonly HashSet<string> Native = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PSN", "RPCS3", "ShadPS4", "Vita3K"
        };

        /// <summary>
        /// Exophase's PlayStation page suffixes, newest first: <c>-psn</c> for recent games, and the
        /// platform itself for older ones ("dark-souls-iii-ps4").
        /// </summary>
        private static readonly string[] Suffixes = { "-psn", "-ps4", "-ps3", "-psvita" };

        public PlayStationMetadataProvider(IPlayniteAPI api, PlayniteAchievementsSettings settings, ILogger logger)
            : base(api, settings, logger)
        {
        }

        public override string Key => MetadataKey;

        public override string DisplayNameKey => "LOCPlayAch_Metadata_Source_PlayStation";

        public override MetadataField SupportedFields => MetadataField.Icons | MetadataField.TrophyTypes | MetadataField.Categories | MetadataField.CategoryArt | MetadataField.CapstoneArt;

        protected override ICollection<string> NativeProviders => Native;

        protected override IReadOnlyList<string> PageSuffixes => Suffixes;

        protected override string PlatformHint => "psn";

        protected override string PreferredPlatform => "PS5";
    }
}
