using System;
using System.Collections.Generic;
using Playnite.SDK;
using PlayniteAchievements.Models.Tagging;
using PlayniteAchievements.Services.Localization;

namespace PlayniteAchievements.Services.Tagging
{
    /// <summary>
    /// Catalog of default tag display names across every shipped localization.
    /// A persisted tag name that matches any entry is treated as un-customized,
    /// allowing it to follow the current Playnite language. Thin adapter over
    /// <see cref="LocalizedDefaultStringCatalog"/>, which holds the shared engine.
    /// </summary>
    public class DefaultTagNameCatalog
    {
        private const string PrefixFormatKey = "LOCPlayAch_Tag_PrefixFormat";

        /// <summary>
        /// Resource key of the status text composed into each default tag name via
        /// <see cref="PrefixFormatKey"/>. Shared with <see cref="TagSyncService"/> so the
        /// catalog and the runtime default-name lookup can never drift.
        /// </summary>
        public static readonly IReadOnlyDictionary<TagType, string> StatusResourceKeys =
            new Dictionary<TagType, string>
            {
                [TagType.HasAchievements] = "LOCPlayAch_Tagging_HasAchievements",
                [TagType.InProgress] = "LOCPlayAch_Filter_InProgress",
                [TagType.Completed] = "LOCPlayAch_Completed",
                [TagType.NoAchievements] = "LOCPlayAch_Tagging_NoAchievements",
                [TagType.Customized] = "LOCPlayAch_Tagging_Customized",
                [TagType.NotCustomized] = "LOCPlayAch_Tagging_NotCustomized",
                [TagType.Excluded] = "LOCPlayAch_ManageAchievements_Status_Excluded",
                [TagType.ExcludedFromSummaries] = "LOCPlayAch_ManageAchievements_Status_ExcludedFromSummaries"
            };

        private readonly LocalizedDefaultStringCatalog _catalog;

        /// <summary>
        /// Builds the catalog from every locale file in <paramref name="localizationDirectory"/>,
        /// plus the hardcoded English fallbacks. A null or missing directory yields a catalog
        /// containing only the hardcoded defaults. Unreadable locale files are skipped.
        /// </summary>
        public DefaultTagNameCatalog(string localizationDirectory, ILogger logger = null)
        {
            var definitions = new List<LocalizedDefaultDefinition>();
            foreach (var kvp in StatusResourceKeys)
            {
                definitions.Add(new LocalizedDefaultDefinition
                {
                    Id = kvp.Key.ToString(),
                    ResourceKeys = new[] { PrefixFormatKey, kvp.Value },
                    Compose = values => string.Format(values[0], values[1]),
                    ExtraHardcodedDefaults = new[] { TaggingSettings.GetDefaultDisplayName(kvp.Key) }
                });
            }

            _catalog = new LocalizedDefaultStringCatalog(localizationDirectory, definitions, logger);
        }

        /// <summary>
        /// Returns true when <paramref name="name"/> equals the default display name of
        /// <paramref name="tagType"/> in any shipped language (trimmed, case-insensitive).
        /// </summary>
        public bool IsKnownDefault(TagType tagType, string name)
        {
            return _catalog.IsKnownDefault(tagType.ToString(), name);
        }

        /// <summary>
        /// Returns the name a tag should be renamed to when its current name is an
        /// un-customized default from any language, or null when it should be left alone
        /// (customized, blank, or already equal to the current default).
        /// </summary>
        public string GetRelocalizedName(TagType tagType, string currentName, string currentDefault)
        {
            return _catalog.GetRelocalizedText(tagType.ToString(), currentName, currentDefault);
        }
    }
}
