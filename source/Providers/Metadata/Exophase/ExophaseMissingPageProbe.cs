using Playnite.SDK;
using PlayniteAchievements.Providers.Exophase;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Metadata.Exophase
{
    /// <summary>
    /// Developer-only check of how Exophase answers an award page that does not exist, next to
    /// one that exists with a translation and one that exists without, in the localized and the
    /// English address. Decides whether a lookup can skip the English load after a localized
    /// address shows the page is missing. Writes report.txt and each page's source under the
    /// output folder.
    /// </summary>
    internal static class ExophaseMissingPageProbe
    {
        /// <summary>Hardcoded gate for the hidden probe menu item. Off in shipped builds.</summary>
        internal static readonly bool Enabled = false;

        private const string Language = "de";

        // (slug, platform, what the refresh log suggests about it)
        private static readonly (string Slug, string Platform, string Expect)[] Cases =
        {
            ("god-of-war-ragnarok-psn", "psn", "exists, translated"),
            ("zz-playach-probe-no-such-game-psn", "psn", "made up, cannot exist"),

            // Round 2: every PlayStation page the reporting refresh matched; each exists.
            ("aliens-vs-predator", "psn", "matched in the log"),
            ("all-trophies-psn", "psn", "matched in the log"),
            ("amnesia-collection-ps4", "psn", "matched in the log"),
            ("avowed-psn", "psn", "matched in the log"),
            ("awaken-astral-blade-psn", "psn", "matched in the log"),
            ("axiom-verge-2-psn-2", "psn", "matched in the log"),
            ("bioshock-infinite-ps4", "psn", "matched in the log"),
            ("castlevania-lords-of-shadow", "psn", "matched in the log"),
            ("castlevania-lords-of-shadow-mirror-of-fate-hd-ps3", "psn", "matched in the log"),
            ("crysis2-remastered-psn-2", "psn", "matched in the log"),
            ("crysis3-remastered-psn-2", "psn", "matched in the log"),
            ("dagon-complete-edition-psn", "psn", "matched in the log"),
            ("darksiders-ii-deathinitive-edition-psn", "psn", "matched in the log"),
            ("darksiders-warmastered-edition-psn", "psn", "matched in the log"),
            ("dark-souls-", "psn", "matched in the log"),
            ("dark-souls-iii-ps4", "psn", "matched in the log"),
            ("darkwood-psn-4", "psn", "matched in the log"),
            ("dead-space-psn", "psn", "matched in the log"),
            ("dear-esther-landmark-edition-ps4", "psn", "matched in the log"),
            ("deus-ex-human-revolution-directors-cut-ps3", "psn", "matched in the log"),
            ("dungeon-siege-iii", "psn", "matched in the log"),
            ("dying-light-2-stay-human-psn", "psn", "matched in the log"),
            ("elex-ii-psn", "psn", "matched in the log"),
            ("fallout-new-vegas", "psn", "matched in the log"),
            ("firewatch-ps4-2", "psn", "matched in the log"),
            ("gothic-1-remake-psn", "psn", "matched in the log"),
            ("icewind-dale-psn", "psn", "matched in the log"),
            ("megaman-legacy-collection-2-psn", "psn", "matched in the log"),
            ("metal-gear-solid-v-ground-zeroes-ps4", "psn", "matched in the log"),
            ("metal-gear-solid-v-the-phantom-pain-ps4", "psn", "matched in the log"),
            ("middle-earth-shadow-of-mordor-game-of-the-year-edition-ps4", "psn", "matched in the log"),
            ("middle-earth-shadow-of-war-psn", "psn", "matched in the log"),
            ("neverwinter-nights-psn", "psn", "matched in the log"),
            ("new-world-aeternum-psn", "psn", "matched in the log"),
            ("nierautomata-ps4", "psn", "matched in the log"),
            ("nocturnal-psn", "psn", "matched in the log"),
            ("pillars-of-eternity-complete-edition-psn", "psn", "matched in the log"),
            ("pillars-of-eternity-ii-deadfire-psn", "psn", "matched in the log"),
            ("rage", "psn", "matched in the log"),
            ("resident-evil-village-psn-2", "psn", "matched in the log"),
            ("seven-enhanced-edition-psn", "psn", "matched in the log"),
            ("shadowrun-hong-kong-extended-edition-psn-2", "psn", "matched in the log"),
            ("skyrim-psn", "psn", "matched in the log"),
            ("spellforce-iii-reforced-psn", "psn", "matched in the log"),
            ("s-t-a-l-k-e-r-call-of-prypiat-psn-3", "psn", "matched in the log"),
            ("s-t-a-l-k-e-r-clear-sky-psn-3", "psn", "matched in the log"),
            ("s-t-a-l-k-e-r-shadow-of-chornobyl-psn-3", "psn", "matched in the log"),
            ("sunless-sea-zubmariner-edition-psn", "psn", "matched in the log"),
            ("the-elder-scrolls-iv-oblivion-remastered-psn", "psn", "matched in the log"),
            ("the-evil-within-ps4-3", "psn", "matched in the log"),
            ("the-stanley-parable-ultra-deluxe-psn-2", "psn", "matched in the log"),
            ("the-vanishing-of-ethan-carter-ps4", "psn", "matched in the log"),
            ("this-war-of-mine-final-cut-psn", "psn", "matched in the log"),
            ("tom-clancys-the-division-ps4", "psn", "matched in the log"),
            ("trine-2-complete-story-ps4-2", "psn", "matched in the log"),
            ("trudograd-psn", "psn", "matched in the log"),
            ("warhammer-40000-mechanicus-psn-2", "psn", "matched in the log"),
            ("watch", "psn", "matched in the log"),
            ("wolfenstein-ii-the-new-colossus-psn-2", "psn", "matched in the log"),
            ("wolfenstein-the-new-order-ps4-2", "psn", "matched in the log")
        };

        public static async Task RunAsync(IPlayniteAPI api, ILogger logger, string folder, CancellationToken ct)
        {
            Directory.CreateDirectory(folder);
            var client = new ExophaseApiClient(api, logger, null);
            var report = new StringBuilder();
            var summary = new StringBuilder();
            report.AppendLine($"Exophase missing-page probe, {DateTime.Now:yyyy-MM-dd HH:mm:ss}, language '{Language}'.");
            report.AppendLine();

            foreach (var (slug, platform, expect) in Cases)
            {
                var english = ExophaseApiClient.BuildUrlFromSlug(slug, platform);
                var localized = ExophaseGamePageParser.LocalizeAwardPageUrl(english, Language);
                report.AppendLine($"== {slug} ({platform}): {expect}");
                summary.Append(slug).Append(": ");
                foreach (var (label, url) in new[] { ("localized", localized), ("english", english) })
                {
                    var (json, html, ms) = await client.ProbeAwardPageAsync(url, ct).ConfigureAwait(false);
                    report.AppendLine($"-- {label} {url} ({ms} ms)");
                    report.AppendLine(json);
                    var status = Regex.Match(json ?? string.Empty, "\"status\":(\\w+)").Groups[1].Value;
                    var rows = Regex.Match(json ?? string.Empty, "\"awardRows\":(\\d+)").Groups[1].Value;
                    summary.Append($"{label}={status}/{rows} ");
                    if (!string.IsNullOrEmpty(html))
                    {
                        var file = Regex.Replace(slug + "_" + label, @"[^A-Za-z0-9_\-]", "_") + ".html";
                        File.WriteAllText(Path.Combine(folder, file), html, Encoding.UTF8);
                        report.AppendLine($"source: {file} ({html.Length} chars)");
                    }
                }

                summary.AppendLine();
                report.AppendLine();
                File.WriteAllText(Path.Combine(folder, "report.txt"), report.ToString(), Encoding.UTF8);
            }

            report.AppendLine("Summary (status/award rows):");
            report.Append(summary);
            report.AppendLine("Done.");
            File.WriteAllText(Path.Combine(folder, "report.txt"), report.ToString(), Encoding.UTF8);
        }
    }
}
