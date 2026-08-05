using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Playnite.SDK;

namespace PlayniteAchievements.Services.Localization
{
    /// <summary>
    /// Describes one user-editable string whose default is composed from localized resources.
    /// The catalog composes the definition against every shipped locale file so a persisted
    /// value can be recognized as "still a default" regardless of the language it was saved in.
    /// </summary>
    public sealed class LocalizedDefaultDefinition
    {
        /// <summary>
        /// Catalog-unique identifier used to look the definition up at query time.
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Resource keys read from each locale file (with en_US fallback per key), passed to
        /// <see cref="Compose"/> in this order.
        /// </summary>
        public IReadOnlyList<string> ResourceKeys { get; set; }

        /// <summary>
        /// Composes one locale's default string from the resolved resource values. A null
        /// return (or a thrown FormatException) skips that locale's entry.
        /// </summary>
        public Func<IReadOnlyList<string>, string> Compose { get; set; }

        /// <summary>
        /// Additional defaults not derived from locale files (e.g. hardcoded English
        /// fallbacks used before resources are available).
        /// </summary>
        public IReadOnlyList<string> ExtraHardcodedDefaults { get; set; }
    }

    /// <summary>
    /// Catalog of composed default strings across every shipped localization. A persisted
    /// string that matches an entry in any language is treated as un-customized, allowing it
    /// to follow the current Playnite language. Extracted from the tag-name relocalization
    /// mechanism so other user-editable localized defaults can share it.
    /// </summary>
    public sealed class LocalizedDefaultStringCatalog
    {
        private const string EnglishFileName = "en_US.xaml";

        private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

        private readonly Dictionary<string, HashSet<string>> _knownDefaults;

        /// <summary>
        /// Builds the catalog from every locale file in <paramref name="localizationDirectory"/>,
        /// plus each definition's hardcoded extras. A null or missing directory yields a
        /// catalog containing only the hardcoded defaults. Unreadable locale files are skipped.
        /// </summary>
        public LocalizedDefaultStringCatalog(
            string localizationDirectory,
            IReadOnlyList<LocalizedDefaultDefinition> definitions,
            ILogger logger = null)
        {
            _knownDefaults = BuildKnownDefaults(localizationDirectory, definitions, logger);
        }

        /// <summary>
        /// Returns true when <paramref name="text"/> equals the composed default of the
        /// definition in any shipped language (trimmed, case-insensitive).
        /// </summary>
        public bool IsKnownDefault(string definitionId, string text)
        {
            return !string.IsNullOrWhiteSpace(text) &&
                definitionId != null &&
                _knownDefaults.TryGetValue(definitionId, out var texts) &&
                texts.Contains(text.Trim());
        }

        /// <summary>
        /// Returns the text a stored value should be replaced with when it is an
        /// un-customized default from any language, or null when it should be left alone
        /// (customized, blank, or already equal to the current default).
        /// </summary>
        public string GetRelocalizedText(string definitionId, string currentText, string currentDefault)
        {
            if (string.IsNullOrWhiteSpace(currentText) || string.IsNullOrWhiteSpace(currentDefault))
            {
                return null;
            }

            if (string.Equals(currentText.Trim(), currentDefault.Trim(), StringComparison.Ordinal))
            {
                return null;
            }

            return IsKnownDefault(definitionId, currentText) ? currentDefault : null;
        }

        private static Dictionary<string, HashSet<string>> BuildKnownDefaults(
            string localizationDirectory,
            IReadOnlyList<LocalizedDefaultDefinition> definitions,
            ILogger logger)
        {
            var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var valid = new List<LocalizedDefaultDefinition>();
            foreach (var definition in definitions ?? new LocalizedDefaultDefinition[0])
            {
                if (string.IsNullOrWhiteSpace(definition?.Id) ||
                    definition.ResourceKeys == null ||
                    definition.Compose == null)
                {
                    continue;
                }

                valid.Add(definition);
                var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var extra in definition.ExtraHardcodedDefaults ?? new string[0])
                {
                    if (!string.IsNullOrWhiteSpace(extra))
                    {
                        known.Add(extra.Trim());
                    }
                }

                result[definition.Id] = known;
            }

            if (valid.Count == 0 ||
                string.IsNullOrWhiteSpace(localizationDirectory) ||
                !Directory.Exists(localizationDirectory))
            {
                return result;
            }

            var wantedKeys = new HashSet<string>(
                valid.SelectMany(definition => definition.ResourceKeys).Where(key => key != null),
                StringComparer.Ordinal);

            var english = ReadStrings(Path.Combine(localizationDirectory, EnglishFileName), wantedKeys, logger);
            foreach (var filePath in Directory.EnumerateFiles(localizationDirectory, "*.xaml"))
            {
                var strings = string.Equals(Path.GetFileName(filePath), EnglishFileName, StringComparison.OrdinalIgnoreCase)
                    ? english
                    : ReadStrings(filePath, wantedKeys, logger);
                if (strings == null)
                {
                    continue;
                }

                AddComposedDefaults(result, valid, strings, english);
            }

            return result;
        }

        private static void AddComposedDefaults(
            Dictionary<string, HashSet<string>> result,
            List<LocalizedDefaultDefinition> definitions,
            Dictionary<string, string> strings,
            Dictionary<string, string> englishFallback)
        {
            foreach (var definition in definitions)
            {
                var values = new string[definition.ResourceKeys.Count];
                var complete = true;
                for (var i = 0; i < definition.ResourceKeys.Count; i++)
                {
                    values[i] = GetStringOrFallback(strings, englishFallback, definition.ResourceKeys[i]);
                    if (values[i] == null)
                    {
                        complete = false;
                        break;
                    }
                }

                if (!complete)
                {
                    continue;
                }

                try
                {
                    var composed = definition.Compose(values);
                    if (!string.IsNullOrWhiteSpace(composed))
                    {
                        result[definition.Id].Add(composed.Trim());
                    }
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
            if (key == null)
            {
                return null;
            }

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

        private static Dictionary<string, string> ReadStrings(
            string filePath,
            HashSet<string> wantedKeys,
            ILogger logger)
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
                    if (key != null && wantedKeys.Contains(key))
                    {
                        result[key] = element.Value;
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, $"Failed to read strings from localization file: {filePath}");
                return null;
            }
        }
    }
}
