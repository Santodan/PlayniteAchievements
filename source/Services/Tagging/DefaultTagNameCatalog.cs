using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using Playnite.SDK;
using PlayniteAchievements.Models.Tagging;

namespace PlayniteAchievements.Services.Tagging
{
    /// <summary>
    /// Catalog of default tag display names across every shipped localization.
    /// A persisted tag name that matches any entry is treated as un-customized,
    /// allowing it to follow the current Playnite language.
    /// </summary>
    public class DefaultTagNameCatalog
    {
        private const string PrefixFormatKey = "LOCPlayAch_Tag_PrefixFormat";
        private const string EnglishFileName = "en_US.xaml";

        private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

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

        private readonly Dictionary<TagType, HashSet<string>> _knownDefaults;

        /// <summary>
        /// Builds the catalog from every locale file in <paramref name="localizationDirectory"/>,
        /// plus the hardcoded English fallbacks. A null or missing directory yields a catalog
        /// containing only the hardcoded defaults. Unreadable locale files are skipped.
        /// </summary>
        public DefaultTagNameCatalog(string localizationDirectory, ILogger logger = null)
        {
            _knownDefaults = BuildKnownDefaults(localizationDirectory, logger);
        }

        /// <summary>
        /// Returns true when <paramref name="name"/> equals the default display name of
        /// <paramref name="tagType"/> in any shipped language (trimmed, case-insensitive).
        /// </summary>
        public bool IsKnownDefault(TagType tagType, string name)
        {
            return !string.IsNullOrWhiteSpace(name) &&
                _knownDefaults.TryGetValue(tagType, out var names) &&
                names.Contains(name.Trim());
        }

        /// <summary>
        /// Returns the name a tag should be renamed to when its current name is an
        /// un-customized default from any language, or null when it should be left alone
        /// (customized, blank, or already equal to the current default).
        /// </summary>
        public string GetRelocalizedName(TagType tagType, string currentName, string currentDefault)
        {
            if (string.IsNullOrWhiteSpace(currentName) || string.IsNullOrWhiteSpace(currentDefault))
            {
                return null;
            }

            if (string.Equals(currentName.Trim(), currentDefault.Trim(), StringComparison.Ordinal))
            {
                return null;
            }

            return IsKnownDefault(tagType, currentName) ? currentDefault : null;
        }

        private static Dictionary<TagType, HashSet<string>> BuildKnownDefaults(
            string localizationDirectory,
            ILogger logger)
        {
            var result = new Dictionary<TagType, HashSet<string>>();
            foreach (TagType tagType in Enum.GetValues(typeof(TagType)))
            {
                result[tagType] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    TaggingSettings.GetDefaultDisplayName(tagType)
                };
            }

            if (string.IsNullOrWhiteSpace(localizationDirectory) || !Directory.Exists(localizationDirectory))
            {
                return result;
            }

            var english = ReadTagStrings(Path.Combine(localizationDirectory, EnglishFileName), logger);
            foreach (var filePath in Directory.EnumerateFiles(localizationDirectory, "*.xaml"))
            {
                var strings = string.Equals(Path.GetFileName(filePath), EnglishFileName, StringComparison.OrdinalIgnoreCase)
                    ? english
                    : ReadTagStrings(filePath, logger);
                if (strings == null)
                {
                    continue;
                }

                AddComposedDefaults(result, strings, english);
            }

            return result;
        }

        private static void AddComposedDefaults(
            Dictionary<TagType, HashSet<string>> result,
            Dictionary<string, string> strings,
            Dictionary<string, string> englishFallback)
        {
            var prefixFormat = GetStringOrFallback(strings, englishFallback, PrefixFormatKey);
            if (prefixFormat == null)
            {
                return;
            }

            foreach (var kvp in StatusResourceKeys)
            {
                var status = GetStringOrFallback(strings, englishFallback, kvp.Value);
                if (status == null)
                {
                    continue;
                }

                try
                {
                    result[kvp.Key].Add(string.Format(prefixFormat, status).Trim());
                }
                catch (FormatException)
                {
                    // Malformed translated format string; skip this locale's entry.
                }
            }
        }

        private static string GetStringOrFallback(
            Dictionary<string, string> strings,
            Dictionary<string, string> englishFallback,
            string key)
        {
            // Mirrors Playnite's resource merge: a key missing from (or blank in) a locale
            // file resolves to the en_US value, so that is the default a user of that
            // locale actually had persisted.
            if (strings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            if (englishFallback != null &&
                englishFallback.TryGetValue(key, out var english) &&
                !string.IsNullOrWhiteSpace(english))
            {
                return english;
            }

            return null;
        }

        private static Dictionary<string, string> ReadTagStrings(string filePath, ILogger logger)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    return null;
                }

                var result = new Dictionary<string, string>(StringComparer.Ordinal);
                var document = XDocument.Load(filePath);
                if (document.Root == null)
                {
                    return result;
                }

                foreach (var element in document.Root.Elements())
                {
                    var key = element.Attribute(XamlNamespace + "Key")?.Value;
                    if (key == null)
                    {
                        continue;
                    }

                    if (key == PrefixFormatKey || StatusResourceKeysContainsValue(key))
                    {
                        result[key] = element.Value;
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, $"Failed to read tag strings from localization file: {filePath}");
                return null;
            }
        }

        private static bool StatusResourceKeysContainsValue(string key)
        {
            foreach (var kvp in StatusResourceKeys)
            {
                if (string.Equals(kvp.Value, key, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
