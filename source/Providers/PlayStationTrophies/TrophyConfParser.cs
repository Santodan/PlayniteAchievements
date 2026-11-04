using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace PlayniteAchievements.Providers.PlayStationTrophies
{
    /// <summary>
    /// Parses NP trophy definitions shared by PS3 and PS Vita trophy sets:
    /// TROPCONF.SFM and TROP_XX.SFM XML documents, and TROPHY.TRP archives.
    /// </summary>
    internal static class TrophyConfParser
    {
        /// <summary>
        /// Parses trophy definitions from TROPCONF.SFM XML file.
        /// </summary>
        /// <param name="tropconfPath">Path to TROPCONF.SFM file.</param>
        /// <param name="language">Target language code (e.g., "en", "fr") for localized names.</param>
        /// <param name="logger">Logger for error reporting.</param>
        /// <returns>List of trophy definitions, or empty list on error.</returns>
        public static List<NpTrophy> ParseTrophyDefinitions(string tropconfPath, string language, ILogger logger)
        {
            var trophies = new List<NpTrophy>();

            if (string.IsNullOrWhiteSpace(tropconfPath) || !File.Exists(tropconfPath))
            {
                return trophies;
            }

            try
            {
                var doc = XDocument.Load(tropconfPath);
                trophies = ParseTrophyConfDocument(doc, language);
            }
            catch (Exception ex)
            {
                logger?.Error(ex, $"[Trophies] Failed to parse TROPCONF.SFM at '{tropconfPath}'");
                return trophies;
            }

            ApplyLocalizedSfmSibling(trophies, tropconfPath, language, logger);
            return trophies;
        }

        /// <summary>
        /// The set's trophies with English text only: names from the TROP_01.SFM sibling, else
        /// from TROPCONF.SFM elements tagged lang="en". Null when neither exists; the
        /// default-language TROP.SFM is never read, since its language varies by set.
        /// </summary>
        public static List<NpTrophy> ParseEnglishTrophyDefinitions(string tropconfPath, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(tropconfPath) || !File.Exists(tropconfPath))
            {
                return null;
            }

            try
            {
                var conf = XDocument.Load(tropconfPath);
                var trophies = ParseTrophyConfDocument(conf, null);
                if (trophies.Count == 0)
                {
                    return null;
                }

                var englishPath = Path.Combine(Path.GetDirectoryName(tropconfPath) ?? string.Empty, "TROP_01.SFM");
                var fromSibling = File.Exists(englishPath);
                var englishDoc = fromSibling ? XDocument.Load(englishPath) : conf;
                var englishById = new Dictionary<int, XElement>();
                foreach (var element in englishDoc.Descendants("trophy"))
                {
                    var id = ParseIntAttribute(element, "id", -1);
                    if (id >= 0 && !englishById.ContainsKey(id))
                    {
                        englishById[id] = element;
                    }
                }

                var found = false;
                foreach (var trophy in trophies)
                {
                    englishById.TryGetValue(trophy.Id, out var element);
                    trophy.Name = ReadEnglishText(element, "name", fromSibling);
                    trophy.Description = ReadEnglishText(element, "detail", fromSibling);
                    found |= trophy.Name.Length > 0;
                }

                return found ? trophies : null;
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, $"[Trophies] Failed to read English trophy text alongside '{tropconfPath}'");
                return null;
            }
        }

        // A TROP_01.SFM element is English whatever its tag; a TROPCONF.SFM one only when tagged "en".
        private static string ReadEnglishText(XElement trophyElement, string elementName, bool fromEnglishFile)
        {
            var element = trophyElement?.Elements(elementName).FirstOrDefault(e =>
                fromEnglishFile || string.Equals(e.Attribute("lang")?.Value?.Trim(), "en", StringComparison.OrdinalIgnoreCase));
            return element?.Value?.Trim() ?? string.Empty;
        }

        /// <summary>
        /// Overlays display text from the TROP_XX.SFM sibling matching the requested
        /// locale, falling back to the default-language TROP.SFM. RPCS3 and Vita3K install
        /// every TRP entry into the trophy folder, so the text files sit next to
        /// TROPCONF.SFM. The TROP.SFM fallback gives display text to a set whose
        /// TROPCONF.SFM carries no name elements, as Vita3K's own trophy viewer reads it.
        /// </summary>
        private static void ApplyLocalizedSfmSibling(
            List<NpTrophy> trophies,
            string tropconfPath,
            string language,
            ILogger logger)
        {
            if (trophies.Count == 0)
            {
                return;
            }

            try
            {
                var folder = Path.GetDirectoryName(tropconfPath);
                if (string.IsNullOrWhiteSpace(folder))
                {
                    return;
                }

                var tropIndex = MapLocaleToTropIndex(language);
                var localizedPath = tropIndex.HasValue
                    ? Path.Combine(folder, $"TROP_{tropIndex.Value:00}.SFM")
                    : null;
                if (localizedPath == null || !File.Exists(localizedPath))
                {
                    localizedPath = Path.Combine(folder, "TROP.SFM");
                }

                if (!File.Exists(localizedPath))
                {
                    return;
                }

                ApplyLocalizedText(trophies, XDocument.Load(localizedPath));
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, $"[Trophies] Failed to apply localized trophy text alongside '{tropconfPath}'");
            }
        }

        /// <summary>
        /// Parses all trophy elements from a trophyconf XML document.
        /// </summary>
        private static List<NpTrophy> ParseTrophyConfDocument(XDocument doc, string language)
        {
            var trophies = new List<NpTrophy>();
            var groupNames = BuildGroupNamesDictionary(doc);

            foreach (var trophyElement in doc.Descendants("trophy"))
            {
                try
                {
                    var trophy = ParseTrophyElement(trophyElement, groupNames, language);
                    if (trophy.Id >= 0)
                    {
                        trophies.Add(trophy);
                    }
                }
                catch
                {
                    // Skip malformed trophy elements
                }
            }

            return trophies;
        }

        /// <summary>
        /// Parses a single trophy element from XML.
        /// </summary>
        private static NpTrophy ParseTrophyElement(XElement trophyElement, Dictionary<string, string> groupNames, string language)
        {
            var gidAttr = trophyElement.Attribute("gid")?.Value;
            var groupId = gidAttr?.Trim() ?? "0";

            return new NpTrophy
            {
                Id = ParseIntAttribute(trophyElement, "id", -1),
                TrophyType = trophyElement.Attribute("ttype")?.Value?.Trim() ?? "B",
                Hidden = string.Equals(trophyElement.Attribute("hidden")?.Value, "yes", StringComparison.OrdinalIgnoreCase),
                Name = GetLocalizedElement(trophyElement, "name", language)?.Trim() ?? string.Empty,
                Description = GetLocalizedElement(trophyElement, "detail", language)?.Trim() ?? string.Empty,
                GroupId = groupId,
                GroupName = groupNames.TryGetValue(groupId, out var name) ? name : null
            };
        }

        /// <summary>
        /// Builds a dictionary mapping group IDs to group names from TROPCONF.SFM.
        /// Used for DLC trophy categorization.
        /// </summary>
        private static Dictionary<string, string> BuildGroupNamesDictionary(XDocument doc)
        {
            var groupNames = new Dictionary<string, string>();

            foreach (var groupElement in doc.Descendants("group"))
            {
                var groupId = groupElement.Attribute("id")?.Value;
                var groupName = groupElement.Element("name")?.Value?.Trim();

                if (!string.IsNullOrWhiteSpace(groupId) && !string.IsNullOrWhiteSpace(groupName))
                {
                    groupNames[groupId] = groupName;
                }
            }

            return groupNames;
        }

        /// <summary>
        /// Extracts the NP Comm ID from a TROPHY.TRP file.
        /// The TROPHY.TRP file contains XML data with the npcommid element.
        /// </summary>
        /// <param name="trophyTrpPath">Path to TROPHY.TRP file.</param>
        /// <param name="logger">Logger for error reporting.</param>
        /// <returns>NP Comm ID string, or null if not found.</returns>
        public static string ExtractNpCommId(string trophyTrpPath, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(trophyTrpPath) || !File.Exists(trophyTrpPath))
            {
                return null;
            }

            try
            {
                var bytes = File.ReadAllBytes(trophyTrpPath);
                return TryReadTrpIdentity(bytes, out var npCommId, out _, logger) ? npCommId : null;
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, $"[Trophies] Failed to extract npcommid from '{trophyTrpPath}'");
                return null;
            }
        }

        /// <summary>
        /// The trophy set's default-language title-name from an installed trophy folder:
        /// TROPCONF.SFM first, then TROP.SFM. Null when neither carries one.
        /// </summary>
        public static string ReadSetTitle(string trophyFolder, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(trophyFolder))
            {
                return null;
            }

            foreach (var fileName in new[] { "TROPCONF.SFM", "TROP.SFM" })
            {
                var path = Path.Combine(trophyFolder, fileName);
                if (!File.Exists(path))
                {
                    continue;
                }

                try
                {
                    var title = ExtractElementText(File.ReadAllText(path, Encoding.UTF8), "title-name");
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        return title;
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    logger?.Debug(ex, $"[Trophies] Failed to read the set title from '{path}'");
                }
            }

            return null;
        }

        /// <summary>
        /// Reads a trophy set's identity (npcommid and title-name) from TROPHY.TRP
        /// bytes: the TROPCONF.SFM entry of a binary archive, or the whole content
        /// for plaintext trophyconf documents. Returns false when no npcommid is found.
        /// </summary>
        public static bool TryReadTrpIdentity(byte[] trpBytes, out string npCommId, out string titleName, ILogger logger = null)
        {
            npCommId = null;
            titleName = null;

            if (trpBytes == null || trpBytes.Length == 0)
            {
                return false;
            }

            try
            {
                string tropconfXml = null;
                IReadOnlyList<TrpEntry> entries = null;

                // Binary TRP archive: search the TROPCONF.SFM entry first.
                if (TrpArchiveReader.HasTrpMagic(trpBytes))
                {
                    entries = TrpArchiveReader.ReadEntries(trpBytes, logger);
                    tropconfXml = entries == null
                        ? null
                        : TrpArchiveReader.ExtractEntryText(trpBytes, entries, "TROPCONF.SFM");
                }

                if (!string.IsNullOrWhiteSpace(tropconfXml))
                {
                    npCommId = ExtractNpCommIdFromText(tropconfXml);
                    titleName = ExtractElementText(tropconfXml, "title-name");
                }

                // Some trophy packs keep names out of TROPCONF.SFM (bare ids/types
                // only); the set title then lives in the default-language TROP.SFM
                // definition entry.
                if (entries != null && string.IsNullOrWhiteSpace(titleName))
                {
                    var tropSfmXml = TrpArchiveReader.ExtractEntryText(trpBytes, entries, "TROP.SFM");
                    if (!string.IsNullOrWhiteSpace(tropSfmXml))
                    {
                        titleName = ExtractElementText(tropSfmXml, "title-name");
                    }
                }

                // Plaintext documents, and archives whose entries carry no id or
                // title, fall back to scanning the whole content.
                if (string.IsNullOrWhiteSpace(npCommId) || string.IsNullOrWhiteSpace(titleName))
                {
                    var fullText = Encoding.UTF8.GetString(trpBytes);
                    if (string.IsNullOrWhiteSpace(npCommId))
                    {
                        npCommId = ExtractNpCommIdFromText(fullText);
                    }

                    if (string.IsNullOrWhiteSpace(titleName))
                    {
                        titleName = ExtractElementText(fullText, "title-name");
                    }
                }

                return !string.IsNullOrWhiteSpace(npCommId);
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "[Trophies] Failed to read TRP identity");
                return false;
            }
        }

        /// <summary>
        /// Extracts an element's inner text from trophyconf XML content,
        /// tolerating surrounding binary noise the same way ExtractNpCommIdFromText does.
        /// </summary>
        private static string ExtractElementText(string content, string elementName)
        {
            var openTag = $"<{elementName}>";
            var tagStart = content.IndexOf(openTag, StringComparison.OrdinalIgnoreCase);
            if (tagStart < 0)
            {
                return null;
            }

            var tagEnd = content.IndexOf($"</{elementName}>", tagStart, StringComparison.OrdinalIgnoreCase);
            if (tagEnd < 0)
            {
                return null;
            }

            var value = content.Substring(tagStart + openTag.Length, tagEnd - tagStart - openTag.Length).Trim();
            return string.IsNullOrWhiteSpace(value) ? null : System.Net.WebUtility.HtmlDecode(value);
        }

        /// <summary>
        /// Finds the npcommid in trophyconf XML text, via element or attribute form.
        /// </summary>
        private static string ExtractNpCommIdFromText(string content)
        {
            // Look for <npcommid>...</npcommid> pattern
            var tagStart = content.IndexOf("<npcommid>", StringComparison.OrdinalIgnoreCase);

            if (tagStart < 0)
            {
                // Try alternate format: npcommid="..."
                var attrStart = content.IndexOf("npcommid=", StringComparison.OrdinalIgnoreCase);
                if (attrStart >= 0)
                {
                    var valueStart = attrStart + "npcommid=".Length;
                    var quoteStart = content.IndexOf("\"", valueStart);
                    if (quoteStart >= 0)
                    {
                        var quoteEnd = content.IndexOf("\"", quoteStart + 1);
                        if (quoteEnd > quoteStart)
                        {
                            return content.Substring(quoteStart + 1, quoteEnd - quoteStart - 1).Trim();
                        }
                    }
                }
                return null;
            }

            var tagEnd = content.IndexOf("</npcommid>", tagStart, StringComparison.OrdinalIgnoreCase);
            if (tagEnd < 0) return null;

            return content.Substring(tagStart + "<npcommid>".Length, tagEnd - tagStart - "<npcommid>".Length).Trim();
        }

        /// <summary>
        /// Parses an integer attribute from an XML element.
        /// </summary>
        private static int ParseIntAttribute(XElement element, string attributeName, int defaultValue)
        {
            var attrValue = element.Attribute(attributeName)?.Value;
            return int.TryParse(attrValue, out var result) ? result : defaultValue;
        }

        /// <summary>
        /// Gets a localized element value from a trophy element.
        /// Tries to find an element with matching lang attribute, falls back to element without lang.
        /// </summary>
        private static string GetLocalizedElement(XElement trophyElement, string elementName, string language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return trophyElement.Element(elementName)?.Value;
            }

            // Try to find element with matching lang attribute
            var localizedElement = trophyElement.Elements(elementName)
                .FirstOrDefault(e => string.Equals(e.Attribute("lang")?.Value, language, StringComparison.OrdinalIgnoreCase));

            if (localizedElement != null)
            {
                return localizedElement.Value;
            }

            // Fall back to element without lang attribute (default language)
            return trophyElement.Elements(elementName)
                .FirstOrDefault(e => e.Attribute("lang") == null)?.Value
                ?? trophyElement.Element(elementName)?.Value;
        }

        /// <summary>
        /// Parses trophy definitions from a TROPHY.TRP file.
        /// Used for pre-launch trophy detection before the emulator copies the trophy set.
        /// </summary>
        /// <param name="trophyTrpPath">Path to TROPHY.TRP file.</param>
        /// <param name="language">Target language code (e.g., "en", "fr") for localized names.</param>
        /// <param name="logger">Logger for error reporting.</param>
        /// <returns>List of trophy definitions (all Unlocked = false), or empty list on error.</returns>
        public static List<NpTrophy> ParseTrophyDefinitionsFromTrp(string trophyTrpPath, string language, ILogger logger)
        {
            var trophies = new List<NpTrophy>();

            if (string.IsNullOrWhiteSpace(trophyTrpPath) || !File.Exists(trophyTrpPath))
            {
                return trophies;
            }

            try
            {
                var bytes = File.ReadAllBytes(trophyTrpPath);

                trophies = ParseTrophiesFromTrpContainer(bytes, language, logger);
                if (trophies.Count == 0)
                {
                    trophies = ParseTrophiesFromPlaintext(bytes, language);
                }

                foreach (var trophy in trophies)
                {
                    trophy.Unlocked = false; // Pre-launch: all locked
                    trophy.UnlockTimeUtc = null;
                }
            }
            catch (Exception ex)
            {
                logger?.Error(ex, $"[Trophies] Failed to parse TROPHY.TRP at '{trophyTrpPath}'");
            }

            return trophies;
        }

        /// <summary>
        /// Parses trophy definitions from a binary TRP archive: structure from
        /// TROPCONF.SFM, display text overlaid from the locale-specific
        /// TROP_XX.SFM (falling back to TROP.SFM).
        /// </summary>
        private static List<NpTrophy> ParseTrophiesFromTrpContainer(byte[] bytes, string language, ILogger logger)
        {
            var trophies = new List<NpTrophy>();

            if (!TrpArchiveReader.HasTrpMagic(bytes))
            {
                return trophies;
            }

            var entries = TrpArchiveReader.ReadEntries(bytes, logger);
            if (entries == null)
            {
                return trophies;
            }

            var tropconfXml = TrpArchiveReader.ExtractEntryText(bytes, entries, "TROPCONF.SFM");
            if (string.IsNullOrWhiteSpace(tropconfXml))
            {
                return trophies;
            }

            try
            {
                trophies = ParseTrophyConfDocument(XDocument.Parse(tropconfXml), language);
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "[Trophies] Failed to parse TROPCONF.SFM entry inside TROPHY.TRP");
                return new List<NpTrophy>();
            }

            if (trophies.Count == 0)
            {
                return trophies;
            }

            var localizedXml = ResolveLocalizedSfmText(bytes, entries, language);
            if (!string.IsNullOrWhiteSpace(localizedXml))
            {
                try
                {
                    ApplyLocalizedText(trophies, XDocument.Parse(localizedXml));
                }
                catch (Exception ex)
                {
                    logger?.Debug(ex, "[Trophies] Failed to parse localized SFM entry inside TROPHY.TRP");
                }
            }

            return trophies;
        }

        /// <summary>
        /// Picks the display-text SFM matching the requested locale
        /// (TROP_XX.SFM), falling back to the default TROP.SFM.
        /// </summary>
        private static string ResolveLocalizedSfmText(byte[] bytes, IReadOnlyList<TrpEntry> entries, string language)
        {
            var tropIndex = MapLocaleToTropIndex(language);
            if (tropIndex.HasValue)
            {
                var localized = TrpArchiveReader.ExtractEntryText(bytes, entries, $"TROP_{tropIndex.Value:00}.SFM");
                if (!string.IsNullOrWhiteSpace(localized))
                {
                    return localized;
                }
            }

            return TrpArchiveReader.ExtractEntryText(bytes, entries, "TROP.SFM");
        }

        /// <summary>
        /// Overlays trophy display names, descriptions, and group names from a
        /// localized trophyconf document onto already-parsed definitions.
        /// </summary>
        private static void ApplyLocalizedText(List<NpTrophy> trophies, XDocument localizedDoc)
        {
            var groupNames = BuildGroupNamesDictionary(localizedDoc);
            var localizedById = new Dictionary<int, XElement>();

            foreach (var trophyElement in localizedDoc.Descendants("trophy"))
            {
                var id = ParseIntAttribute(trophyElement, "id", -1);
                if (id >= 0 && !localizedById.ContainsKey(id))
                {
                    localizedById[id] = trophyElement;
                }
            }

            foreach (var trophy in trophies)
            {
                if (localizedById.TryGetValue(trophy.Id, out var element))
                {
                    var name = element.Element("name")?.Value?.Trim();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        trophy.Name = name;
                    }

                    var detail = element.Element("detail")?.Value?.Trim();
                    if (!string.IsNullOrWhiteSpace(detail))
                    {
                        trophy.Description = detail;
                    }
                }

                if (groupNames.TryGetValue(trophy.GroupId ?? "0", out var groupName) &&
                    !string.IsNullOrWhiteSpace(groupName))
                {
                    trophy.GroupName = groupName;
                }
            }
        }

        /// <summary>
        /// Legacy fallback for TRP-like files that are plain XML rather than a
        /// binary archive: extracts the first trophyconf document by text search.
        /// </summary>
        private static List<NpTrophy> ParseTrophiesFromPlaintext(byte[] bytes, string language)
        {
            var content = Encoding.UTF8.GetString(bytes);

            // The root tag may carry attributes (e.g. <trophyconf version="1.1">),
            // so match on the tag prefix only.
            var tagStart = content.IndexOf("<trophyconf", StringComparison.OrdinalIgnoreCase);
            if (tagStart < 0) return new List<NpTrophy>();

            var tagEnd = content.IndexOf("</trophyconf>", tagStart, StringComparison.OrdinalIgnoreCase);
            if (tagEnd < 0) return new List<NpTrophy>();

            var xmlContent = content.Substring(tagStart, tagEnd - tagStart + "</trophyconf>".Length);
            return ParseTrophyConfDocument(XDocument.Parse(xmlContent), language);
        }

        /// <summary>
        /// Maps a trophy locale code (as returned by MapGlobalLanguageToTrophyLocale)
        /// to the SCE numeric language id used in TROP_XX.SFM entry names.
        /// Returns null when the locale has no SCE language id (falls back to TROP.SFM).
        /// </summary>
        private static int? MapLocaleToTropIndex(string trophyLocale)
        {
            if (string.IsNullOrWhiteSpace(trophyLocale))
            {
                return null;
            }

            switch (trophyLocale.Trim().ToLowerInvariant())
            {
                case "ja": return 0;
                case "en": return 1;
                case "fr": return 2;
                case "es": return 3;
                case "de": return 4;
                case "it": return 5;
                case "nl": return 6;
                case "pt": return 7;
                case "ru": return 8;
                case "ko": return 9;
                case "zh": return 11; // Simplified Chinese; 10 is Traditional
                case "fi": return 12;
                case "sv": return 13;
                case "da": return 14;
                case "no": return 15;
                case "pl": return 16;
                case "pt-br": return 17;
                case "tr": return 19;
                default: return null;
            }
        }

        /// <summary>
        /// Maps a global language setting to a trophy locale code (the SCE language set shared by PS3 and PS Vita).
        /// </summary>
        /// <param name="globalLanguage">The global language setting (e.g., "english", "french").</param>
        /// <returns>Trophy locale code (e.g., "en", "fr"), or null for default.</returns>
        public static string MapGlobalLanguageToTrophyLocale(string globalLanguage)
        {
            if (string.IsNullOrWhiteSpace(globalLanguage))
            {
                return null;
            }

            return globalLanguage.Trim().ToLowerInvariant() switch
            {
                "english" => "en",
                "french" => "fr",
                "spanish" => "es",
                "german" => "de",
                "italian" => "it",
                "japanese" => "ja",
                "dutch" => "nl",
                "portuguese" => "pt",
                "russian" => "ru",
                "korean" => "ko",
                "chinese" => "zh",
                "polish" => "pl",
                "danish" => "da",
                "finnish" => "fi",
                "norwegian" => "no",
                "swedish" => "sv",
                "turkish" => "tr",
                "czech" => "cs",
                "hungarian" => "hu",
                "greek" => "el",
                "brazilian" => "pt-br",
                "latam" => "es-419",
                _ => null
            };
        }

    }
}
