using HtmlAgilityPack;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PlayniteAchievements.Providers.Hypixel
{
    /// <summary>
    /// Pure parsing helpers for the Hypixel provider, isolated from HTTP and Playnite
    /// dependencies so they can be unit tested directly.
    /// </summary>
    internal static class HypixelParsing
    {
        internal const string SiteBase = "https://hypixel.net";

        private const string PanelClassPrefix = "game-";
        private const string LegacySectionMarker = "(Legacy)";

        /// <summary>
        /// Profile panel slugs whose game key in <c>/v2/resources/achievements</c> is not the slug
        /// with its hyphens removed. Listed from the page and the resource keys on 2026-09-30.
        /// </summary>
        private static readonly Dictionary<string, string> CatalogKeyBySlug =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["murder-mystery"] = "murdermystery",
                ["arcade-games"] = "arcade",
                ["uhc-champions"] = "uhc",
                ["arena-brawl"] = "arena",
                ["build-battle"] = "buildbattle",
                ["cops-and-crims"] = "copsandcrims",
                ["megawalls"] = "walls3",
                ["paintball-warfare"] = "paintball",
                ["quakecraft"] = "quake",
                ["blitz-survivalgames"] = "blitz",
                ["smash-heroes"] = "supersmash",
                ["tnt-games"] = "tntgames",
                ["turbo-kart-racers"] = "gingerbread",
                ["wool-games"] = "woolgames",
                ["crazywalls"] = "truecombat"
            };

        /// <summary>
        /// True for a Playnite game whose name contains "Hypixel". The server has no library
        /// entry of its own, so a user adds one by hand (or binds another game with the override).
        /// </summary>
        public static bool IsHypixelTitle(string name)
        {
            return !string.IsNullOrWhiteSpace(name) &&
                   name.IndexOf("hypixel", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// The <c>/v2/resources/achievements</c> game key for a profile panel slug.
        /// </summary>
        public static string ResolveCatalogGameKey(string slug)
        {
            if (string.IsNullOrWhiteSpace(slug))
            {
                return null;
            }

            var trimmed = slug.Trim();
            return CatalogKeyBySlug.TryGetValue(trimmed, out var key)
                ? key
                : trimmed.Replace("-", string.Empty).ToLowerInvariant();
        }

        /// <summary>
        /// Profile page URL for a username. The page takes the name as typed; the site matches it
        /// case-insensitively.
        /// </summary>
        public static Uri BuildProfileUri(string username)
        {
            return new Uri(SiteBase + "/player/" + Uri.EscapeDataString(username.Trim()) + "/achievements");
        }

        /// <summary>
        /// Parses the public achievements page into panels and rows. A page without any game
        /// panel (a changed layout, or an interstitial served in its place) yields an empty
        /// profile, which callers must not treat as "no achievements".
        /// </summary>
        public static HypixelProfile ParseProfile(string html)
        {
            var profile = new HypixelProfile();
            if (string.IsNullOrWhiteSpace(html))
            {
                return profile;
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var panels = doc.DocumentNode.SelectNodes("//div[contains(concat(' ', normalize-space(@class), ' '), ' panel ')]");
            if (panels == null)
            {
                return profile;
            }

            foreach (var panelNode in panels)
            {
                var slug = ReadPanelSlug(panelNode);
                if (slug == null)
                {
                    continue;
                }

                var panel = new HypixelProfilePanel
                {
                    Slug = slug,
                    DisplayName = ReadHeading(panelNode, slug),
                    IconUrl = ReadHeadingIcon(panelNode)
                };

                var sections = panelNode.SelectNodes(".//div[contains(concat(' ', normalize-space(@class), ' '), ' section ')]");
                if (sections != null)
                {
                    foreach (var section in sections)
                    {
                        var title = HtmlEntity.DeEntitize(section.SelectSingleNode("./h3")?.InnerText ?? string.Empty);
                        var legacy = title.IndexOf(LegacySectionMarker, StringComparison.OrdinalIgnoreCase) >= 0;

                        var items = section.SelectNodes(".//li[@data-name]");
                        if (items == null)
                        {
                            continue;
                        }

                        foreach (var item in items)
                        {
                            panel.Entries.Add(ReadEntry(item, legacy));
                        }
                    }
                }

                profile.Panels.Add(panel);
            }

            return profile;
        }

        /// <summary>
        /// Substitutes a tier's threshold into a tiered description ("Collect %%value%% wool").
        /// </summary>
        public static string FormatTieredDescription(string description, long? amount)
        {
            if (string.IsNullOrEmpty(description) || !amount.HasValue)
            {
                return description;
            }

            return description.Replace("%%value%%", amount.Value.ToString("N0", CultureInfo.CurrentCulture));
        }

        private static string ReadPanelSlug(HtmlNode panelNode)
        {
            var classes = panelNode.GetAttributeValue("class", string.Empty)
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var gameClass = classes.FirstOrDefault(c =>
                c.StartsWith(PanelClassPrefix, StringComparison.OrdinalIgnoreCase) &&
                c.Length > PanelClassPrefix.Length);
            return gameClass?.Substring(PanelClassPrefix.Length).ToLowerInvariant();
        }

        private static string ReadHeading(HtmlNode panelNode, string fallback)
        {
            var text = HtmlEntity.DeEntitize(panelNode.SelectSingleNode("./h2")?.InnerText ?? string.Empty).Trim();
            return string.IsNullOrEmpty(text) ? fallback : text;
        }

        private static string ReadHeadingIcon(HtmlNode panelNode)
        {
            var src = panelNode.SelectSingleNode("./h2//img")?.GetAttributeValue("src", null);
            return ToAbsoluteUrl(src);
        }

        private static HypixelProfileEntry ReadEntry(HtmlNode item, bool legacy)
        {
            var classes = item.GetAttributeValue("class", string.Empty)
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            return new HypixelProfileEntry
            {
                Name = ReadText(item, "data-name"),
                Description = ReadText(item, "data-description"),
                Completed = classes.Contains("completed", StringComparer.OrdinalIgnoreCase),
                Legacy = legacy,
                Tier = ReadInt(item, "data-tier"),
                Progress = ReadLong(item, "data-progress"),
                Amount = ReadLong(item, "data-amount")
            };
        }

        private static string ReadText(HtmlNode node, string attribute)
        {
            var raw = node.GetAttributeValue(attribute, null);
            return raw == null ? null : HtmlEntity.DeEntitize(raw).Trim();
        }

        private static int? ReadInt(HtmlNode node, string attribute)
        {
            var raw = node.GetAttributeValue(attribute, null);
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : (int?)null;
        }

        private static long? ReadLong(HtmlNode node, string attribute)
        {
            var raw = node.GetAttributeValue(attribute, null);
            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : (long?)null;
        }

        private static string ToAbsoluteUrl(string src)
        {
            if (string.IsNullOrWhiteSpace(src))
            {
                return null;
            }

            var trimmed = src.Trim();
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute) &&
                (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
            {
                return absolute.ToString();
            }

            return trimmed.StartsWith("/", StringComparison.Ordinal) ? SiteBase + trimmed : null;
        }
    }
}
