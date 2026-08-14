using Playnite.SDK;
using PlayniteAchievements.Providers.RPCS3.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace PlayniteAchievements.Providers.RPCS3
{
    /// <summary>
    /// Static class for parsing RPCS3 trophy files.
    /// Handles TROPCONF.SFM (XML definitions) and TROPUSR.DAT (binary unlock data).
    /// </summary>
    internal static class Rpcs3TrophyParser
    {
        // TROPUSR.DAT is a big-endian table file. These layouts mirror RPCS3's
        // TROPUSRHeader, TROPUSRTableHeader, and TROPUSREntry6 definitions.
        private const uint TropusrMagic = 0x818F54AD;
        private const int TropusrHeaderSize = 0x30;
        private const int TropusrTableHeaderSize = 0x20;
        private const uint TrophyStateTableType = 6;
        private const uint TrophyStateEntryContentsSize = 0x60;
        private const int TrophyStateEntryHeaderSize = 0x10;

        // Magic-byte patterns used by the live in-game progress reader (below), which
        // scans TROPUSR.DAT heuristically while a game is running; the authoritative
        // unlock-state table is parsed separately by TryReadTropusrStates.
        private static readonly byte[][] ProgressMagicBytePatterns =
        {
            new byte[] { 0, 0, 0, 4, 0, 0, 0, 0x50, 0, 0, 0 },
            new byte[] { 0, 0, 0, 6, 0, 0, 0, 0x60, 0, 0, 0 }
        };
        private const int TrophyStateEntrySize = TrophyStateEntryHeaderSize + (int)TrophyStateEntryContentsSize;

        /// <summary>
        /// Parses trophy definitions from TROPCONF.SFM XML file.
        /// </summary>
        /// <param name="tropconfPath">Path to TROPCONF.SFM file.</param>
        /// <param name="language">Target language code (e.g., "en", "fr") for localized names.</param>
        /// <param name="logger">Logger for error reporting.</param>
        /// <returns>List of trophy definitions, or empty list on error.</returns>
        public static List<Rpcs3Trophy> ParseTrophyDefinitions(string tropconfPath, string language, ILogger logger)
        {
            var trophies = new List<Rpcs3Trophy>();

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
                logger?.Error(ex, $"[RPCS3] Failed to parse TROPCONF.SFM at '{tropconfPath}'");
                return trophies;
            }

            ApplyLocalizedSfmSibling(trophies, tropconfPath, language, logger);
            return trophies;
        }

        /// <summary>
        /// Overlays display text from the TROP_XX.SFM sibling matching the requested
        /// locale. RPCS3 installs every TRP entry into the trophy folder, so the
        /// localized files sit next to the language-neutral TROPCONF.SFM.
        /// </summary>
        private static void ApplyLocalizedSfmSibling(
            List<Rpcs3Trophy> trophies,
            string tropconfPath,
            string language,
            ILogger logger)
        {
            var tropIndex = MapPs3LocaleToTropIndex(language);
            if (trophies.Count == 0 || !tropIndex.HasValue)
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

                var localizedPath = Path.Combine(folder, $"TROP_{tropIndex.Value:00}.SFM");
                if (!File.Exists(localizedPath))
                {
                    return;
                }

                ApplyLocalizedText(trophies, XDocument.Load(localizedPath));
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, $"[RPCS3] Failed to apply localized trophy text alongside '{tropconfPath}'");
            }
        }

        /// <summary>
        /// Parses all trophy elements from a trophyconf XML document.
        /// </summary>
        private static List<Rpcs3Trophy> ParseTrophyConfDocument(XDocument doc, string language)
        {
            var trophies = new List<Rpcs3Trophy>();
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
        private static Rpcs3Trophy ParseTrophyElement(XElement trophyElement, Dictionary<string, string> groupNames, string language)
        {
            var gidAttr = trophyElement.Attribute("gid")?.Value;
            var groupId = gidAttr?.Trim() ?? "0";

            return new Rpcs3Trophy
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
        /// Parses trophy unlock data from TROPUSR.DAT binary file.
        /// Updates the provided trophy list with unlock status and timestamps.
        /// </summary>
        /// <param name="tropusrPath">Path to TROPUSR.DAT file.</param>
        /// <param name="trophies">List of trophies to update with unlock data.</param>
        /// <param name="logger">Logger for error reporting.</param>
        public static void ParseTrophyUnlockData(string tropusrPath, List<Rpcs3Trophy> trophies, ILogger logger)
        {
            TryParseTrophyUnlockData(tropusrPath, trophies, logger);
        }

        /// <summary>
        /// Parses the authoritative unlock-state table in TROPUSR.DAT. Returns
        /// false without changing <paramref name="trophies"/> when the file is
        /// malformed, incomplete, or internally inconsistent. Callers use that
        /// distinction to preserve previously known progress instead of treating a
        /// failed parse as an all-locked trophy list.
        /// </summary>
        internal static bool TryParseTrophyUnlockData(string tropusrPath, List<Rpcs3Trophy> trophies, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(tropusrPath) || !File.Exists(tropusrPath) || trophies == null || trophies.Count == 0)
            {
                return false;
            }

            try
            {
                var bytes = File.ReadAllBytes(tropusrPath);
                if (!TryReadTropusrStates(bytes, out var states, out var error))
                {
                    logger?.Warn($"[RPCS3] Ignoring invalid TROPUSR.DAT at '{tropusrPath}': {error}. Existing achievement progress will be preserved.");
                    return false;
                }

                var trophyById = trophies
                    .Where(trophy => trophy != null)
                    .GroupBy(trophy => trophy.Id)
                    .ToDictionary(group => group.Key, group => group.First());

                var unmatchedStateIds = states.Keys
                    .Where(id => !trophyById.ContainsKey(id))
                    .OrderBy(id => id)
                    .ToList();

                // Do not mutate a definition until every table and record has been
                // checked. This keeps an invalid file from producing partial state.
                foreach (var state in states)
                {
                    if (!trophyById.TryGetValue(state.Key, out var trophy))
                    {
                        continue;
                    }

                    trophy.Unlocked = state.Value.Unlocked;
                    trophy.UnlockTimeUtc = state.Value.UnlockTimeUtc;
                }

                if (unmatchedStateIds.Count > 0)
                {
                    var reportedIds = string.Join(", ", unmatchedStateIds.Take(16));
                    var suffix = unmatchedStateIds.Count > 16 ? ", ..." : string.Empty;
                    logger?.Warn(
                        $"[RPCS3] TROPUSR.DAT at '{tropusrPath}' has {unmatchedStateIds.Count} state record(s) " +
                        $"not present in its trophy definitions: [{reportedIds}{suffix}].");
                }

                return true;
            }
            catch (Exception ex)
            {
                logger?.Error(ex, $"[RPCS3] Failed to parse TROPUSR.DAT at '{tropusrPath}'; existing achievement progress will be preserved.");
                return false;
            }
        }

        private static bool TryReadTropusrStates(
            byte[] bytes,
            out Dictionary<int, TrophyUnlockState> states,
            out string error)
        {
            states = null;
            error = null;

            if (bytes == null || bytes.Length < TropusrHeaderSize)
            {
                error = "file is shorter than the TROPUSR header";
                return false;
            }

            if (ReadUInt32BigEndian(bytes, 0) != TropusrMagic)
            {
                error = "unexpected file magic";
                return false;
            }

            var tableCount = ReadUInt32BigEndian(bytes, 8);
            if (tableCount == 0 || tableCount > (uint)((bytes.Length - TropusrHeaderSize) / TropusrTableHeaderSize))
            {
                error = "invalid table count";
                return false;
            }

            var table6Seen = false;
            var parsedStates = new Dictionary<int, TrophyUnlockState>();

            for (var tableIndex = 0; tableIndex < tableCount; tableIndex++)
            {
                var tableOffset = TropusrHeaderSize + ((long)tableIndex * TropusrTableHeaderSize);
                if (!HasRange(bytes, (ulong)tableOffset, TropusrTableHeaderSize))
                {
                    error = "table header extends beyond the file";
                    return false;
                }

                var type = ReadUInt32BigEndian(bytes, (int)tableOffset);
                var contentsSize = ReadUInt32BigEndian(bytes, (int)tableOffset + 4);
                var entryCount = ReadUInt32BigEndian(bytes, (int)tableOffset + 12);
                var entriesOffset = ReadUInt64BigEndian(bytes, (int)tableOffset + 16);

                if (contentsSize > int.MaxValue - TrophyStateEntryHeaderSize)
                {
                    error = "entry size is too large";
                    return false;
                }

                var entrySize = (long)contentsSize + TrophyStateEntryHeaderSize;
                if (entrySize <= 0 ||
                    entriesOffset > (ulong)bytes.Length ||
                    entryCount > 0 &&
                    ((ulong)entrySize > (ulong)bytes.Length ||
                     (ulong)entryCount > ((ulong)bytes.Length - entriesOffset) / (ulong)entrySize))
                {
                    error = "table entries extend beyond the file";
                    return false;
                }

                if (type != TrophyStateTableType)
                {
                    continue;
                }

                if (table6Seen || contentsSize != TrophyStateEntryContentsSize)
                {
                    error = table6Seen ? "multiple trophy-state tables" : "unexpected trophy-state entry size";
                    return false;
                }

                table6Seen = true;
                for (var entryIndex = 0; entryIndex < entryCount; entryIndex++)
                {
                    var entryOffset = entriesOffset + ((ulong)entryIndex * (ulong)TrophyStateEntrySize);
                    if (!HasRange(bytes, entryOffset, TrophyStateEntrySize))
                    {
                        error = "trophy-state entry extends beyond the file";
                        return false;
                    }

                    if (ReadUInt32BigEndian(bytes, (int)entryOffset) != TrophyStateTableType ||
                        ReadUInt32BigEndian(bytes, (int)entryOffset + 4) != TrophyStateEntryContentsSize)
                    {
                        error = "invalid trophy-state entry header";
                        return false;
                    }

                    var trophyId = ReadUInt32BigEndian(bytes, (int)entryOffset + 16);
                    if (trophyId > int.MaxValue || parsedStates.ContainsKey((int)trophyId))
                    {
                        error = trophyId > int.MaxValue ? "trophy id is out of range" : "duplicate trophy id";
                        return false;
                    }

                    var trophyState = ReadUInt32BigEndian(bytes, (int)entryOffset + 20);
                    var timestamp2 = ReadUInt64BigEndian(bytes, (int)entryOffset + 40);
                    DateTime? unlockTimeUtc = null;
                    if (trophyState != 0 && timestamp2 > 0)
                    {
                        if (timestamp2 > (ulong)(DateTime.MaxValue.Ticks / 10))
                        {
                            error = "unlock timestamp is out of range";
                            return false;
                        }

                        unlockTimeUtc = new DateTime((long)(timestamp2 * 10), DateTimeKind.Utc);
                    }

                    parsedStates.Add((int)trophyId, new TrophyUnlockState
                    {
                        Unlocked = trophyState != 0,
                        UnlockTimeUtc = unlockTimeUtc
                    });
                }
            }

            if (!table6Seen)
            {
                error = "trophy-state table is missing";
                return false;
            }

            states = parsedStates;
            return true;
        }

        private static bool HasRange(byte[] bytes, ulong offset, long length)
        {
            return bytes != null &&
                   length >= 0 &&
                   offset <= (ulong)bytes.Length &&
                   (ulong)length <= (ulong)bytes.Length - offset;
        }

        private static uint ReadUInt32BigEndian(byte[] bytes, int offset)
        {
            return ((uint)bytes[offset] << 24) |
                   ((uint)bytes[offset + 1] << 16) |
                   ((uint)bytes[offset + 2] << 8) |
                   bytes[offset + 3];
        }

        private static ulong ReadUInt64BigEndian(byte[] bytes, int offset)
        {
            return ((ulong)bytes[offset] << 56) |
                   ((ulong)bytes[offset + 1] << 48) |
                   ((ulong)bytes[offset + 2] << 40) |
                   ((ulong)bytes[offset + 3] << 32) |
                   ((ulong)bytes[offset + 4] << 24) |
                   ((ulong)bytes[offset + 5] << 16) |
                   ((ulong)bytes[offset + 6] << 8) |
                   bytes[offset + 7];
        }

        private sealed class TrophyUnlockState
        {
            public bool Unlocked { get; set; }
            public DateTime? UnlockTimeUtc { get; set; }
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
                logger?.Debug(ex, $"[RPCS3] Failed to extract npcommid from '{trophyTrpPath}'");
                return null;
            }
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

                // Binary TRP archive: search the TROPCONF.SFM entry first.
                if (Rpcs3TrpArchiveReader.HasTrpMagic(trpBytes))
                {
                    var entries = Rpcs3TrpArchiveReader.ReadEntries(trpBytes, logger);
                    tropconfXml = entries == null
                        ? null
                        : Rpcs3TrpArchiveReader.ExtractEntryText(trpBytes, entries, "TROPCONF.SFM");
                }

                if (!string.IsNullOrWhiteSpace(tropconfXml))
                {
                    npCommId = ExtractNpCommIdFromText(tropconfXml);
                    titleName = ExtractElementText(tropconfXml, "title-name");
                }

                // Plaintext documents, and archives whose TROPCONF entry carries
                // no id, fall back to scanning the whole content.
                if (string.IsNullOrWhiteSpace(npCommId))
                {
                    var fullText = Encoding.UTF8.GetString(trpBytes);
                    npCommId = ExtractNpCommIdFromText(fullText);
                    if (string.IsNullOrWhiteSpace(titleName))
                    {
                        titleName = ExtractElementText(fullText, "title-name");
                    }
                }

                return !string.IsNullOrWhiteSpace(npCommId);
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "[RPCS3] Failed to read TRP identity");
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
        /// Used for pre-launch trophy detection before RPCS3 creates cache files.
        /// </summary>
        /// <param name="trophyTrpPath">Path to TROPHY.TRP file.</param>
        /// <param name="language">Target language code (e.g., "en", "fr") for localized names.</param>
        /// <param name="logger">Logger for error reporting.</param>
        /// <returns>List of trophy definitions (all Unlocked = false), or empty list on error.</returns>
        public static List<Rpcs3Trophy> ParseTrophyDefinitionsFromTrp(string trophyTrpPath, string language, ILogger logger)
        {
            var trophies = new List<Rpcs3Trophy>();

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
                logger?.Error(ex, $"[RPCS3] Failed to parse TROPHY.TRP at '{trophyTrpPath}'");
            }

            return trophies;
        }

        /// <summary>
        /// Parses trophy definitions from a binary TRP archive: structure from
        /// TROPCONF.SFM, display text overlaid from the locale-specific
        /// TROP_XX.SFM (falling back to TROP.SFM).
        /// </summary>
        private static List<Rpcs3Trophy> ParseTrophiesFromTrpContainer(byte[] bytes, string language, ILogger logger)
        {
            var trophies = new List<Rpcs3Trophy>();

            if (!Rpcs3TrpArchiveReader.HasTrpMagic(bytes))
            {
                return trophies;
            }

            var entries = Rpcs3TrpArchiveReader.ReadEntries(bytes, logger);
            if (entries == null)
            {
                return trophies;
            }

            var tropconfXml = Rpcs3TrpArchiveReader.ExtractEntryText(bytes, entries, "TROPCONF.SFM");
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
                logger?.Debug(ex, "[RPCS3] Failed to parse TROPCONF.SFM entry inside TROPHY.TRP");
                return new List<Rpcs3Trophy>();
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
                    logger?.Debug(ex, "[RPCS3] Failed to parse localized SFM entry inside TROPHY.TRP");
                }
            }

            return trophies;
        }

        /// <summary>
        /// Picks the display-text SFM matching the requested locale
        /// (TROP_XX.SFM), falling back to the default TROP.SFM.
        /// </summary>
        private static string ResolveLocalizedSfmText(byte[] bytes, IReadOnlyList<Rpcs3TrpEntry> entries, string language)
        {
            var tropIndex = MapPs3LocaleToTropIndex(language);
            if (tropIndex.HasValue)
            {
                var localized = Rpcs3TrpArchiveReader.ExtractEntryText(bytes, entries, $"TROP_{tropIndex.Value:00}.SFM");
                if (!string.IsNullOrWhiteSpace(localized))
                {
                    return localized;
                }
            }

            return Rpcs3TrpArchiveReader.ExtractEntryText(bytes, entries, "TROP.SFM");
        }

        /// <summary>
        /// Overlays trophy display names, descriptions, and group names from a
        /// localized trophyconf document onto already-parsed definitions.
        /// </summary>
        private static void ApplyLocalizedText(List<Rpcs3Trophy> trophies, XDocument localizedDoc)
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
        private static List<Rpcs3Trophy> ParseTrophiesFromPlaintext(byte[] bytes, string language)
        {
            var content = Encoding.UTF8.GetString(bytes);

            // The root tag may carry attributes (e.g. <trophyconf version="1.1">),
            // so match on the tag prefix only.
            var tagStart = content.IndexOf("<trophyconf", StringComparison.OrdinalIgnoreCase);
            if (tagStart < 0) return new List<Rpcs3Trophy>();

            var tagEnd = content.IndexOf("</trophyconf>", tagStart, StringComparison.OrdinalIgnoreCase);
            if (tagEnd < 0) return new List<Rpcs3Trophy>();

            var xmlContent = content.Substring(tagStart, tagEnd - tagStart + "</trophyconf>".Length);
            return ParseTrophyConfDocument(XDocument.Parse(xmlContent), language);
        }

        /// <summary>
        /// Maps a PS3 locale code (as returned by MapGlobalLanguageToPs3Locale)
        /// to the SCE numeric language id used in TROP_XX.SFM entry names.
        /// Returns null when the locale has no PS3 language id (falls back to TROP.SFM).
        /// </summary>
        private static int? MapPs3LocaleToTropIndex(string ps3Locale)
        {
            if (string.IsNullOrWhiteSpace(ps3Locale))
            {
                return null;
            }

            switch (ps3Locale.Trim().ToLowerInvariant())
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
        /// Maps a global language setting to PS3 locale code.
        /// </summary>
        /// <param name="globalLanguage">The global language setting (e.g., "english", "french").</param>
        /// <returns>PS3 locale code (e.g., "en", "fr"), or null for default.</returns>
        public static string MapGlobalLanguageToPs3Locale(string globalLanguage)
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

        /// <summary>
        /// Progress-only TROPHY reader used while a game is running. Trophy ids come from the
        /// existing cache schema, so this never reads TROPCONF or any icon metadata.
        /// </summary>
        internal static bool TryParseTrophyProgress(
            string tropusrPath,
            IReadOnlyCollection<int> trophyIds,
            out Dictionary<int, DateTime?> unlockedById)
        {
            unlockedById = new Dictionary<int, DateTime?>();
            var ids = (trophyIds ?? Array.Empty<int>())
                .Where(id => id >= 0)
                .Distinct()
                .OrderBy(id => id)
                .ToList();
            if (string.IsNullOrWhiteSpace(tropusrPath) || !File.Exists(tropusrPath) || ids.Count == 0)
            {
                return false;
            }

            try
            {
                byte[] bytes;
                using (var stream = new FileStream(
                    tropusrPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    bytes = memory.ToArray();
                }

                var entryOffsets = FindProgressEntryOffsets(bytes);
                if (entryOffsets.Count < ids.Count)
                {
                    return false;
                }

                var relevantOffsets = entryOffsets
                    .Skip(entryOffsets.Count - ids.Count)
                    .ToList();
                var parsedIds = new HashSet<int>();
                for (var index = 0; index < relevantOffsets.Count; index++)
                {
                    var entryOffset = relevantOffsets[index];
                    var entryEnd = index + 1 < relevantOffsets.Count
                        ? relevantOffsets[index + 1] - ProgressMagicBytePatterns[0].Length
                        : bytes.Length;
                    if (entryOffset < 0 || entryEnd - entryOffset < 29)
                    {
                        unlockedById.Clear();
                        return false;
                    }

                    var trophyId = bytes[entryOffset];
                    if (!ids.Contains(trophyId))
                    {
                        unlockedById.Clear();
                        return false;
                    }

                    parsedIds.Add(trophyId);
                    var unlocked =
                        bytes[entryOffset + 9] == 0 &&
                        bytes[entryOffset + 10] == 0 &&
                        bytes[entryOffset + 11] == 0 &&
                        bytes[entryOffset + 12] == 1;
                    if (unlocked)
                    {
                        DateTime? unlockTimeUtc = null;
                        ulong rawTimestamp = 0;
                        for (var timestampIndex = 22; timestampIndex < 29; timestampIndex++)
                        {
                            rawTimestamp =
                                (rawTimestamp << 8) |
                                bytes[entryOffset + timestampIndex];
                        }

                        if (rawTimestamp > 0 && rawTimestamp <= (ulong)(long.MaxValue / 10))
                        {
                            var ticks = (long)rawTimestamp * 10L;
                            if (ticks > 0 && ticks < DateTime.MaxValue.Ticks)
                            {
                                unlockTimeUtc = new DateTime(ticks, DateTimeKind.Utc);
                            }
                        }

                        unlockedById[trophyId] = unlockTimeUtc;
                    }
                }

                if (parsedIds.Count != ids.Count)
                {
                    unlockedById.Clear();
                    return false;
                }

                return true;
            }
            catch
            {
                unlockedById.Clear();
                return false;
            }
        }

        private static List<int> FindProgressEntryOffsets(byte[] bytes)
        {
            var offsets = new List<int>();
            if (bytes == null || bytes.Length < ProgressMagicBytePatterns[0].Length)
            {
                return offsets;
            }

            for (var index = 0; index <= bytes.Length - ProgressMagicBytePatterns[0].Length; index++)
            {
                foreach (var pattern in ProgressMagicBytePatterns)
                {
                    var matches = true;
                    for (var patternIndex = 0; patternIndex < pattern.Length; patternIndex++)
                    {
                        if (bytes[index + patternIndex] != pattern[patternIndex])
                        {
                            matches = false;
                            break;
                        }
                    }

                    if (!matches)
                    {
                        continue;
                    }

                    offsets.Add(index + pattern.Length);
                    index += pattern.Length - 1;
                    break;
                }
            }

            return offsets;
        }
    }
}
