using HtmlAgilityPack;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Providers.Metadata.TrueGaming
{
    /// <summary>One achievement row of a TrueAchievements, TrueTrophies or TrueSteamAchievements list page.</summary>
    internal sealed class TrueGamingAchievement
    {
        /// <summary>The site's achievement id from its link ("a477200", "t316928").</summary>
        public string Id { get; set; }

        public string Name { get; set; }

        public string Description { get; set; }

        /// <summary>The DLC pack the row is listed under; null for the base game.</summary>
        public string Dlc { get; set; }

        /// <summary>The site's flag names on the row ("Missable", "Single Player").</summary>
        public List<string> Flags { get; } = new List<string>();

        /// <summary>The score shown before the description (<c>data-bf="70 - "</c>): gamerscore on TrueAchievements.</summary>
        public int? Points { get; set; }

        /// <summary>The rank in the description's class (<c>class="t g"</c>) on TrueTrophies: "platinum", "gold", "silver" or "bronze".</summary>
        public string TrophyType { get; set; }
    }

    internal sealed class TrueGamingPage
    {
        public List<TrueGamingAchievement> Achievements { get; } = new List<TrueGamingAchievement>();

        /// <summary>The game's own image (the page's <c>og:image</c>); null when it has none.</summary>
        public string GameArt { get; set; }

        /// <summary>DLC pack name to its image URL.</summary>
        public Dictionary<string, string> DlcArt { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>False when the row flags could not be paired with the page's flag list; rows then carry none.</summary>
        public bool FlagsDecoded { get; set; }

        /// <summary>
        /// True when the site answered with a page, with or without achievements; false for a
        /// load that failed or a challenge page, which say nothing about whether the page exists.
        /// </summary>
        public bool Loaded { get; set; }
    }

    /// <summary>
    /// Reads a TrueGaming achievement list page. Each row carries its flags only as a 32-bit mask
    /// (<c>class="flg-58000000"</c>); the page's flag filter lists the flags present with their ids
    /// and counts. Mask bits are ordered like the flag ids, so the page's set bits, most significant
    /// first, pair with its flag ids in ascending order, and each pair's row count is checked
    /// against the filter's count.
    /// </summary>
    internal static class TrueGamingPageParser
    {
        private static readonly Regex RowId = new Regex(@"^\d+_\d+_\d+$", RegexOptions.CultureInvariant);
        private static readonly Regex AchievementLink = new Regex(@"^/([at]\d+)/", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex FlagMask = new Regex(@"\bflg-([0-9a-fA-F]{8})\b", RegexOptions.CultureInvariant);
        private static readonly Regex FilterInput = new Regex(@"^chkFlag_(\d+)$", RegexOptions.CultureInvariant);
        private static readonly Regex FilterLabel = new Regex(@"^\s*([\d,]+)\s+(.+?)\s*$", RegexOptions.CultureInvariant | RegexOptions.Singleline);
        private static readonly Regex LeadingScore = new Regex(@"^\s*([\d,]+)\s*-", RegexOptions.CultureInvariant);
        private static readonly Regex ShareImage = new Regex(@"ImagerShare\.share\('([^']+)'", RegexOptions.CultureInvariant);

        public static TrueGamingPage Parse(string html)
        {
            var page = new TrueGamingPage();
            if (string.IsNullOrWhiteSpace(html))
            {
                return page;
            }

            var document = new HtmlDocument();
            document.LoadHtml(html);
            var gameArt = document.DocumentNode
                .SelectSingleNode("//meta[@property='og:image']")
                ?.GetAttributeValue("content", null);
            page.GameArt = string.IsNullOrWhiteSpace(gameArt) ? null : WebUtility.HtmlDecode(gameArt).Trim();

            var masks = new List<uint>();
            string dlc = null;
            foreach (var node in document.DocumentNode.Descendants())
            {
                if (node.NodeType != HtmlNodeType.Element)
                {
                    continue;
                }

                if (node.Name == "div" && HasClass(node, "pnl-hd") && HasClass(node, "game"))
                {
                    dlc = HasClass(node, "dlc") ? ReadDlc(node, page) : null;
                    continue;
                }

                if (node.Name == "li" && RowId.IsMatch(node.GetAttributeValue("id", string.Empty)) &&
                    node.ParentNode != null && HasClass(node.ParentNode, "ach-panels"))
                {
                    var row = ReadRow(node, dlc, out var mask);
                    if (row != null)
                    {
                        page.Achievements.Add(row);
                        masks.Add(mask);
                    }
                }
            }

            var flags = DecodeFlags(ReadFilter(document), masks);
            page.FlagsDecoded = flags != null;
            if (flags != null)
            {
                for (var i = 0; i < page.Achievements.Count; i++)
                {
                    page.Achievements[i].Flags.AddRange(flags
                        .Where(flag => (masks[i] & flag.Bit) != 0)
                        .Select(flag => flag.Name));
                }
            }

            return page;
        }

        private static string ReadDlc(HtmlNode header, TrueGamingPage page)
        {
            var name = Text(header.SelectSingleNode(".//h2"));
            if (name == null)
            {
                return null;
            }

            var share = header.Descendants("span")
                .Select(span => ShareImage.Match(span.GetAttributeValue("onclick", string.Empty)))
                .FirstOrDefault(match => match.Success);
            if (share != null && !page.DlcArt.ContainsKey(name))
            {
                page.DlcArt[name] = WebUtility.HtmlDecode(share.Groups[1].Value);
            }

            return name;
        }

        private static TrueGamingAchievement ReadRow(HtmlNode item, string dlc, out uint mask)
        {
            mask = 0;
            var title = item.ChildNodes.FirstOrDefault(child => child.Name == "a" && HasClass(child, "title"));
            var link = AchievementLink.Match(title?.GetAttributeValue("href", string.Empty) ?? string.Empty);
            var name = Text(title);
            if (!link.Success || name == null)
            {
                return null;
            }

            var flagNode = item.Descendants("i")
                .Select(node => FlagMask.Match(node.GetAttributeValue("class", string.Empty)))
                .FirstOrDefault(match => match.Success);
            if (flagNode != null)
            {
                mask = uint.Parse(flagNode.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }

            var description = item.ChildNodes.FirstOrDefault(child => child.Name == "p");
            var score = LeadingScore.Match(description?.GetAttributeValue("data-bf", string.Empty) ?? string.Empty);
            return new TrueGamingAchievement
            {
                Id = link.Groups[1].Value.ToLowerInvariant(),
                Name = name,
                Description = Text(description),
                Dlc = dlc,
                Points = score.Success &&
                         int.TryParse(score.Groups[1].Value.Replace(",", string.Empty), NumberStyles.Integer, CultureInfo.InvariantCulture, out var points)
                    ? points
                    : (int?)null,
                TrophyType = description != null && HasClass(description, "t") ? ReadTrophyType(description) : null
            };
        }

        private static string ReadTrophyType(HtmlNode description) =>
            HasClass(description, "p") ? "platinum"
            : HasClass(description, "g") ? "gold"
            : HasClass(description, "s") ? "silver"
            : HasClass(description, "b") ? "bronze"
            : null;

        /// <summary>The page's flag filter: (flag id, name, row count) for each flag present.</summary>
        private static List<(int Id, string Name, int Count)> ReadFilter(HtmlDocument document)
        {
            var filter = new List<(int Id, string Name, int Count)>();
            foreach (var input in document.DocumentNode.Descendants("input"))
            {
                var id = FilterInput.Match(input.GetAttributeValue("id", string.Empty));
                if (!id.Success)
                {
                    continue;
                }

                var label = input.ParentNode?.ChildNodes.FirstOrDefault(child =>
                    child.Name == "label" &&
                    string.Equals(child.GetAttributeValue("for", string.Empty), input.Id, StringComparison.Ordinal));
                var text = FilterLabel.Match(WebUtility.HtmlDecode(label?.InnerText ?? string.Empty));
                if (!text.Success ||
                    !int.TryParse(text.Groups[1].Value.Replace(",", string.Empty), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
                {
                    continue;
                }

                filter.Add((int.Parse(id.Groups[1].Value, CultureInfo.InvariantCulture), text.Groups[2].Value, count));
            }

            return filter;
        }

        // Set on some rows of all three sites but never listed in the flag filter.
        private const uint UnlistedBit = 0x00000008;

        /// <summary>Each flag's name and mask bit, or null when the page's bits and flag list disagree.</summary>
        internal static List<(string Name, uint Bit)> DecodeFlags(
            IReadOnlyList<(int Id, string Name, int Count)> filter,
            IReadOnlyList<uint> masks)
        {
            var union = masks.Aggregate(0u, (all, mask) => all | mask) & ~UnlistedBit;
            var bits = Enumerable.Range(0, 32)
                .Select(shift => 1u << (31 - shift))
                .Where(bit => (union & bit) != 0)
                .ToList();
            var flags = filter.GroupBy(flag => flag.Id).Select(group => group.First()).OrderBy(flag => flag.Id).ToList();
            if (bits.Count == 0 || bits.Count != flags.Count)
            {
                return null;
            }

            var decoded = new List<(string Name, uint Bit)>();
            for (var i = 0; i < bits.Count; i++)
            {
                var bit = bits[i];
                if (masks.Count(mask => (mask & bit) != 0) != flags[i].Count)
                {
                    return null;
                }

                decoded.Add((flags[i].Name, bit));
            }

            return decoded;
        }

        private static bool HasClass(HtmlNode node, string name) =>
            node.GetAttributeValue("class", string.Empty)
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Contains(name, StringComparer.Ordinal);

        private static string Text(HtmlNode node)
        {
            var text = node == null ? null : WebUtility.HtmlDecode(node.InnerText ?? string.Empty).Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
    }
}
