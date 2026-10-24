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
        internal static readonly bool Enabled = true;

        private const string Language = "de";

        // (slug, platform, what the refresh log suggests about it)
        private static readonly (string Slug, string Platform, string Expect)[] Cases =
        {
            ("god-of-war-ragnarok-psn", "psn", "exists, translated"),
            ("fallout-4-xbox-one", "xbox", "exists, translated"),
            ("alan-wake-remastered-ps4", "psn", "German missed, English may exist"),
            ("assassins-creed-3-psvita", "psn", "German missed, English may exist"),
            ("amnesia-the-dark-psvita", "psn", "German missed, English may exist"),
            ("aliens-vs-predator-psvita", "psn", "German missed, English may exist"),
            ("against-the-black-priory-ps3", "psn", "both missed in the log"),
            ("alan-wake-remastered-psvita", "psn", "both missed in the log"),
            ("wolfenstein-2-psvita", "psn", "both missed in the log"),
            ("warhammer-40k-mechanicus-ps3", "psn", "both missed in the log"),
            ("zz-playach-probe-no-such-game-psn", "psn", "made up, cannot exist"),
            ("zz-playach-probe-no-such-game-xbox-one", "xbox", "made up, cannot exist")
        };

        public static async Task RunAsync(IPlayniteAPI api, ILogger logger, string folder, CancellationToken ct)
        {
            Directory.CreateDirectory(folder);
            var client = new ExophaseApiClient(api, logger, null);
            var report = new StringBuilder();
            report.AppendLine($"Exophase missing-page probe, {DateTime.Now:yyyy-MM-dd HH:mm:ss}, language '{Language}'.");
            report.AppendLine();

            foreach (var (slug, platform, expect) in Cases)
            {
                var english = ExophaseApiClient.BuildUrlFromSlug(slug, platform);
                var localized = ExophaseGamePageParser.LocalizeAwardPageUrl(english, Language);
                report.AppendLine($"== {slug} ({platform}): {expect}");
                foreach (var (label, url) in new[] { ("localized", localized), ("english", english) })
                {
                    var (json, html, ms) = await client.ProbeAwardPageAsync(url, ct).ConfigureAwait(false);
                    report.AppendLine($"-- {label} {url} ({ms} ms)");
                    report.AppendLine(json);
                    if (!string.IsNullOrEmpty(html))
                    {
                        var file = Regex.Replace(slug + "_" + label, @"[^A-Za-z0-9_\-]", "_") + ".html";
                        File.WriteAllText(Path.Combine(folder, file), html, Encoding.UTF8);
                        report.AppendLine($"source: {file} ({html.Length} chars)");
                    }
                }

                report.AppendLine();
                File.WriteAllText(Path.Combine(folder, "report.txt"), report.ToString(), Encoding.UTF8);
            }

            report.AppendLine("Done.");
            File.WriteAllText(Path.Combine(folder, "report.txt"), report.ToString(), Encoding.UTF8);
        }
    }
}
