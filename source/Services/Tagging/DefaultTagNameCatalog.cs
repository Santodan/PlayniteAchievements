using System;
using System.Collections.Generic;
using System.Linq;
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
                [TagType.HasAchievements] = "LOCPlayAch_Tagging_PlatformAchievements",
                [TagType.InProgress] = "LOCPlayAch_Filter_InProgress",
                [TagType.Completed] = "LOCPlayAch_Completed",
                [TagType.NoAchievements] = "LOCPlayAch_Tagging_NoAchievements",
                [TagType.CustomAchievements] = "LOCPlayAch_Tagging_CustomAchievements",
                [TagType.Customized] = "LOCPlayAch_Tagging_Customized",
                [TagType.NotCustomized] = "LOCPlayAch_Tagging_NotCustomized",
                [TagType.Excluded] = "LOCPlayAch_ManageAchievements_Status_Excluded",
                [TagType.ExcludedFromSummaries] = "LOCPlayAch_ManageAchievements_Status_ExcludedFromSummaries"
            };

        /// <summary>
        /// Names a kind's default had before it was renamed, in every language, so a tag still
        /// called by one counts as un-customized and is renamed to the current default.
        /// </summary>
        private static readonly IReadOnlyDictionary<TagType, (string ResourceKey, string English)> FormerResourceKeys =
            new Dictionary<TagType, (string ResourceKey, string English)>
            {
                [TagType.HasAchievements] = ("LOCPlayAch_Tagging_HasAchievements", "[PA] Has Achievements")
            };

        private static string FormerId(TagType tagType) => tagType + ".Former";

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

            foreach (var tagType in CustomizationTagAreas.All.SelectMany(area => area.TagTypes))
            {
                definitions.Add(BuildAreaDefinition(tagType));
            }

            foreach (var kvp in FormerResourceKeys)
            {
                definitions.Add(new LocalizedDefaultDefinition
                {
                    Id = FormerId(kvp.Key),
                    ResourceKeys = new[] { PrefixFormatKey, kvp.Value.ResourceKey },
                    Compose = values => string.Format(values[0], values[1]),
                    ExtraHardcodedDefaults = new[] { kvp.Value.English }
                });
            }

            _catalog = new LocalizedDefaultStringCatalog(localizationDirectory, definitions, logger);
        }

        /// <summary>
        /// Resource keys of a customization area kind's name, in composition order: the tag
        /// prefix and the kind's own whole name (never a format around a shared noun, which
        /// cannot agree in gender or case across languages).
        /// </summary>
        public static string[] GetAreaResourceKeys(TagType tagType)
        {
            return CustomizationTagAreas.TryGetArea(tagType, out _, out _)
                ? new[] { PrefixFormatKey, CustomizationTagAreas.NameResourceKey(tagType) }
                : null;
        }

        /// <summary>Composes an area kind's name from the values of <see cref="GetAreaResourceKeys"/>.</summary>
        public static string ComposeAreaName(IReadOnlyList<string> values)
        {
            return string.Format(values[0], values[1]);
        }

        private static LocalizedDefaultDefinition BuildAreaDefinition(TagType tagType)
        {
            return new LocalizedDefaultDefinition
            {
                Id = tagType.ToString(),
                ResourceKeys = GetAreaResourceKeys(tagType),
                Compose = ComposeAreaName,
                ExtraHardcodedDefaults = null
            };
        }

        /// <summary>
        /// Returns true when <paramref name="name"/> equals the default display name of
        /// <paramref name="tagType"/> in any shipped language (trimmed, case-insensitive).
        /// </summary>
        public bool IsKnownDefault(TagType tagType, string name)
        {
            return _catalog.IsKnownDefault(tagType.ToString(), name) ||
                   FormerResourceKeys.ContainsKey(tagType) && _catalog.IsKnownDefault(FormerId(tagType), name);
        }

        /// <summary>
        /// Returns the name a tag should be renamed to when its current name is an
        /// un-customized default from any language, or null when it should be left alone
        /// (customized, blank, or already equal to the current default).
        /// </summary>
        public string GetRelocalizedName(TagType tagType, string currentName, string currentDefault)
        {
            return _catalog.GetRelocalizedText(tagType.ToString(), currentName, currentDefault) ??
                   (FormerResourceKeys.ContainsKey(tagType)
                       ? _catalog.GetRelocalizedText(FormerId(tagType), currentName, currentDefault)
                       : null);
        }
    }
}
