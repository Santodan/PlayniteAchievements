using Playnite.SDK;
using PlayniteAchievements.Models;
using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Providers.Metadata
{
    /// <summary>
    /// Xbox achievement icons and gamerscore for games another provider services, read from
    /// Exophase's public achievement pages, preferring the Xbox Series page.
    /// </summary>
    internal sealed class XboxMetadataProvider : ExophasePageMetadataProvider
    {
        public const string MetadataKey = "Xbox";

        private static readonly HashSet<string> Native = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Xbox", "Xenia"
        };

        /// <summary>
        /// Exophase's Xbox page suffixes: both <c>-xbox</c> and <c>-xbox-one</c> for current
        /// consoles, and <c>-xbox-360</c> for older games.
        /// </summary>
        private static readonly string[] Suffixes = { "-xbox", "-xbox-one", "-xbox-360" };

        public XboxMetadataProvider(IPlayniteAPI api, PlayniteAchievementsSettings settings, ILogger logger)
            : base(api, settings, logger)
        {
        }

        public override string Key => MetadataKey;

        public override string DisplayNameKey => "LOCPlayAch_Provider_Xbox";

        public override MetadataField SupportedFields => MetadataField.Icons | MetadataField.Points;

        protected override ICollection<string> NativeProviders => Native;

        protected override IReadOnlyList<string> PageSuffixes => Suffixes;

        protected override string PlatformHint => "xbox";

        protected override string PreferredPlatform => "Xbox Series";
    }
}
