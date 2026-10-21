using Playnite.SDK;
using PlayniteAchievements.Common;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Metadata.TrueGaming
{
    /// <summary>
    /// Developer-only check of whether TrueAchievements, TrueTrophies and TrueSteamAchievements pages
    /// load in an offscreen view past Cloudflare, and what their achievement markup carries. Logs one
    /// line per page and saves each page source next to the log for parser fixtures.
    /// </summary>
    internal static class TrueGamingProbe
    {
        /// <summary>Hardcoded gate for the hidden "Probe TrueGaming" menu item. Off in shipped builds.</summary>
        internal static readonly bool Enabled = true;

        private const int Attempts = 3;
        private const int RetryDelayMs = 3000;
        private const int NavigationTimeoutMs = 30000;

        private static readonly string[] Urls =
        {
            // Elden Ring Nightreign on each site: the search (real slugs) and the list page the
            // lookup's slug opens, with the final address showing any redirect.
            "https://www.trueachievements.com/true-api/sitesearch/search?q=Elden%20Ring%20Nightreign&category=Games",
            "https://www.trueachievements.com/game/ELDEN-RING-NIGHTREIGN/achievements",
            "https://www.truetrophies.com/true-api/sitesearch/search?q=Elden%20Ring%20Nightreign&category=Games",
            "https://www.truetrophies.com/game/ELDEN-RING-NIGHTREIGN/trophies",
            "https://www.truetrophies.com/game/ELDEN-RING-NIGHTREIGN-PS5/trophies",
            "https://truesteamachievements.com/true-api/sitesearch/search?q=Elden%20Ring%20Nightreign&category=Games",
            "https://truesteamachievements.com/game/ELDEN-RING-NIGHTREIGN/achievements"
        };

        // Games released on more than one platform: each is looked up on every site the way the
        // provider does, and the rows found are compared by name for flags and DLC pack.
        private static readonly string[] CompareGames =
        {
            "ELDEN RING NIGHTREIGN", "ELDEN RING", "Cyberpunk 2077", "Hades", "Fallout 4",
            "Hollow Knight", "Baldur's Gate 3", "Clair Obscur: Expedition 33", "PEAK"
        };

        private static readonly Regex AchievementItem = new Regex(@"<li[^>]*\bid=""\d+_\d+_\d+""", RegexOptions.CultureInvariant);
        private static readonly Regex FlagClass = new Regex(@"class=""flg-([0-9a-fA-F]{8})""", RegexOptions.CultureInvariant);
        private static readonly Regex FlagFilter = new Regex(
            @"id=""chkFlag_(\d+)""[^>]*/>\s*<label[^>]*>\s*<i[^>]*></i>\s*<b>([\d,]+)</b>\s*([^<]+)</label>",
            RegexOptions.CultureInvariant);
        private static readonly Regex DlcHeader = new Regex(@"class=""pnl-hd[^""]*\bdlc\b", RegexOptions.CultureInvariant);
        private static readonly Regex Title = new Regex(@"<title>([^<]*)</title>", RegexOptions.CultureInvariant);

        public static async Task RunAsync(IPlayniteAPI api, ILogger logger, string outputDirectory, CancellationToken ct)
        {
            Directory.CreateDirectory(outputDirectory);
            var views = new OffscreenViewLeaseSource(api, logger);
            using (views.BeginLease())
            {
                for (var i = 0; i < Urls.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var url = Urls[i];
                    try
                    {
                        await ProbeAsync(views, logger, url, Path.Combine(outputDirectory, $"{i:00}-{FileStem(url)}.html"), ct)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger?.Warn(ex, $"[TrueGamingProbe] {url} failed");
                    }
                }

                foreach (var game in CompareGames)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        await CompareAsync(views, logger, game, outputDirectory, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger?.Warn(ex, $"[TrueGamingProbe] compare '{game}' failed");
                    }
                }
            }

            logger?.Info($"[TrueGamingProbe] done; pages saved to {outputDirectory}");
        }

        private static async Task ProbeAsync(OffscreenViewLeaseSource views, ILogger logger, string url, string file, CancellationToken ct)
        {
            var watch = Stopwatch.StartNew();
            var (html, address, attempts) = await views.WithNavigableViewAsync(async view =>
            {
                await view.NavigateAndWaitAsync(url, NavigationTimeoutMs).ConfigureAwait(false);
                var source = await view.GetPageSourceAsync().ConfigureAwait(false);
                var attempt = 1;
                while (IsChallenge(source) && attempt < Attempts)
                {
                    await Task.Delay(RetryDelayMs, ct).ConfigureAwait(false);
                    source = await view.GetPageSourceAsync().ConfigureAwait(false);
                    attempt++;
                }

                // A JSON endpoint renders as a text document; keep the text, not the wrapping markup.
                if (url.IndexOf("/true-api/", StringComparison.OrdinalIgnoreCase) >= 0 && !IsChallenge(source))
                {
                    source = await view.GetPageTextAsync().ConfigureAwait(false);
                }

                return (source ?? string.Empty, view.GetCurrentAddress(), attempt);
            }, ct).ConfigureAwait(false);

            File.WriteAllText(file, html, Encoding.UTF8);

            var masks = FlagClass.Matches(html).Cast<Match>().Select(m => m.Groups[1].Value.ToLowerInvariant()).ToList();
            var filters = FlagFilter.Matches(html).Cast<Match>()
                .Select(m => $"{m.Groups[1].Value}:{m.Groups[3].Value.Trim()}({m.Groups[2].Value})")
                .ToList();
            var title = Title.Match(html);
            var excerpt = html.Length > 2048 ? html.Substring(0, 2048) : html;

            logger?.Info(
                $"[TrueGamingProbe] {url} -> {address} | {watch.ElapsedMilliseconds} ms, attempts={attempts}, " +
                $"bytes={html.Length}, challenge={IsChallenge(html)}, title=\"{(title.Success ? title.Groups[1].Value.Trim() : "")}\", " +
                $"items={AchievementItem.Matches(html).Count}, flagged={masks.Count}, distinctMasks={masks.Distinct().Count()}, " +
                $"dlcHeaders={DlcHeader.Matches(html).Count}, filters=[{string.Join(", ", filters)}]");

            if (url.IndexOf("/true-api/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                logger?.Info($"[TrueGamingProbe] api excerpt: {excerpt.Replace("\r", " ").Replace("\n", " ")}");
            }
        }

        /// <summary>
        /// Finds <paramref name="game"/> on each site, then writes compare-&lt;slug&gt;.txt: per site the
        /// page and its counts, then per pair of sites the rows matched by name whose flags or DLC
        /// pack differ.
        /// </summary>
        private static async Task CompareAsync(OffscreenViewLeaseSource views, ILogger logger, string game, string outputDirectory, CancellationToken ct)
        {
            var found = new List<(TrueGamingSite Site, string Slug, string Address, TrueGamingPage Page)>();
            foreach (var site in TrueGamingSite.LookupOrder)
            {
                foreach (var slug in TrueGamingLinks.CandidateSlugs(new[] { game }, site.Suffixes))
                {
                    ct.ThrowIfCancellationRequested();
                    var url = site.ListUrl(slug);
                    var (html, address) = await views.WithNavigableViewAsync(async view =>
                    {
                        await view.NavigateAndWaitAsync(url, NavigationTimeoutMs).ConfigureAwait(false);
                        var source = await view.GetPageSourceAsync().ConfigureAwait(false);
                        for (var attempt = 1; IsChallenge(source) && attempt < Attempts; attempt++)
                        {
                            await Task.Delay(RetryDelayMs, ct).ConfigureAwait(false);
                            source = await view.GetPageSourceAsync().ConfigureAwait(false);
                        }

                        return (source ?? string.Empty, view.GetCurrentAddress());
                    }, ct).ConfigureAwait(false);

                    var page = TrueGamingPageParser.Parse(html);
                    if (page.Achievements.Count > 0)
                    {
                        File.WriteAllText(Path.Combine(outputDirectory, $"compare-{site.Prefix}-{slug}.html"), html, Encoding.UTF8);
                        found.Add((site, slug, address, page));
                        break;
                    }
                }
            }

            var report = new StringBuilder();
            report.AppendLine(game);
            foreach (var entry in found)
            {
                var flagged = entry.Page.Achievements.Count(row => row.Flags.Count > 0);
                var packs = entry.Page.Achievements.Select(row => row.Dlc).Where(dlc => dlc != null).Distinct().ToList();
                report.AppendLine(
                    $"  {entry.Site.Prefix}:{entry.Slug} -> {entry.Address} | rows={entry.Page.Achievements.Count}, flagsDecoded={entry.Page.FlagsDecoded}, " +
                    $"flagged={flagged}, dlc=[{string.Join("; ", packs)}]");
            }

            for (var i = 0; i < found.Count; i++)
            {
                for (var j = i + 1; j < found.Count; j++)
                {
                    CompareRows(report, found[i].Site.Prefix, found[i].Page, found[j].Site.Prefix, found[j].Page);
                }
            }

            var file = Path.Combine(outputDirectory, $"compare-{TrueGamingLinks.ToSlug(game)}.txt");
            File.WriteAllText(file, report.ToString(), Encoding.UTF8);
            logger?.Info($"[TrueGamingProbe] compare {report.ToString().Replace("\r", "").Replace("\n", " || ")}");
        }

        private static void CompareRows(StringBuilder report, string leftName, TrueGamingPage left, string rightName, TrueGamingPage right)
        {
            var rightRows = right.Achievements
                .GroupBy(row => NameKey(row.Name))
                .ToDictionary(group => group.Key, group => group.First());
            int matched = 0, sameFlags = 0, sameDlc = 0;
            var differences = new List<string>();
            foreach (var row in left.Achievements)
            {
                if (!rightRows.TryGetValue(NameKey(row.Name), out var other))
                {
                    continue;
                }

                matched++;
                var leftFlags = string.Join(", ", row.Flags.OrderBy(flag => flag, StringComparer.OrdinalIgnoreCase));
                var rightFlags = string.Join(", ", other.Flags.OrderBy(flag => flag, StringComparer.OrdinalIgnoreCase));
                var flagsSame = string.Equals(leftFlags, rightFlags, StringComparison.OrdinalIgnoreCase);
                var dlcSame = string.Equals(row.Dlc ?? "", other.Dlc ?? "", StringComparison.OrdinalIgnoreCase);
                sameFlags += flagsSame ? 1 : 0;
                sameDlc += dlcSame ? 1 : 0;
                if (!flagsSame)
                {
                    differences.Add($"    {row.Name}: flags {leftName}=[{leftFlags}] {rightName}=[{rightFlags}]");
                }

                if (!dlcSame)
                {
                    differences.Add($"    {row.Name}: dlc {leftName}=[{row.Dlc}] {rightName}=[{other.Dlc}]");
                }
            }

            report.AppendLine(
                $"  {leftName} vs {rightName}: matched {matched} of {left.Achievements.Count}/{right.Achievements.Count}, " +
                $"same flags {sameFlags}, same dlc {sameDlc}");
            foreach (var line in differences)
            {
                report.AppendLine(line);
            }
        }

        private static string NameKey(string name) =>
            new string((name ?? string.Empty).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        private static bool IsChallenge(string html) =>
            string.IsNullOrEmpty(html) ||
            html.IndexOf("Just a moment", StringComparison.OrdinalIgnoreCase) >= 0 ||
            html.IndexOf("cf-chl", StringComparison.OrdinalIgnoreCase) >= 0 ||
            html.IndexOf("Attention Required", StringComparison.OrdinalIgnoreCase) >= 0;

        private static string FileStem(string url)
        {
            var uri = new Uri(url);
            var stem = (uri.Host.Replace("www.", "") + uri.AbsolutePath + uri.Query).Trim('/');
            return Regex.Replace(stem, @"[^A-Za-z0-9]+", "-").Trim('-');
        }
    }
}
