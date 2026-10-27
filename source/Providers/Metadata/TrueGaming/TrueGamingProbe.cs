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
        internal static readonly bool Enabled = false;

        // The site and comparison checks of earlier probes.
        private static readonly bool RunSiteChecks = false;

        // The language check of an earlier probe.
        private static readonly bool RunLanguageChecks = false;

        // Games whose sites name some achievements differently: each site's rows, what Combine
        // makes of them, and which rows the sites share a description for but not a name.
        private static readonly string[] MergeGames = { "AI-The-Somnium-Files" };

        // How the sites choose the language of achievement titles: what the browser sends, what
        // each page says, and whether an address parameter changes the titles.
        private static readonly string[] LanguageUrls =
        {
            "https://httpbin.org/headers",
            "https://www.trueachievements.com/game/ELDEN-RING/achievements",
            "https://www.trueachievements.com/game/Halo-Infinite/achievements",
            "https://www.truetrophies.com/game/ELDEN-RING/trophies",
            "https://truesteamachievements.com/game/ELDEN-RING/achievements",
            "https://www.trueachievements.com/game/ELDEN-RING/achievements?lang=de",
            "https://www.trueachievements.com/game/ELDEN-RING/achievements?language=de",
            "https://www.trueachievements.com/game/ELDEN-RING/achievements?culture=de-DE",
            "https://www.trueachievements.com/game/ELDEN-RING/achievements?l=de"
        };

        private const string LanguageScript =
            "(function(){try{" +
            "var titles=[].slice.call(document.querySelectorAll('ul.ach-panels > li a.title')).slice(0,5).map(function(a){return a.textContent.trim();});" +
            "var ui=[].slice.call(document.querySelectorAll('select,[hreflang],[data-lang],a[href*=lang],a[href*=Lang],a[href*=language],a[href*=Language]')).slice(0,20)" +
            ".map(function(e){return e.tagName+' '+(e.getAttribute('name')||e.getAttribute('id')||'')+' '+(e.getAttribute('href')||e.getAttribute('hreflang')||e.getAttribute('data-lang')||'')+' '+(e.textContent||'').trim().slice(0,40);});" +
            "return JSON.stringify({htmlLang:document.documentElement.lang,navigator:navigator.language,languages:(navigator.languages||[]).join(','),cookie:document.cookie,titles:titles,languageUi:ui});" +
            "}catch(e){return 'error: '+e;}})()";

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
            "Hollow Knight", "Baldur's Gate 3", "Clair Obscur: Expedition 33", "PEAK",
            "Hogwarts Legacy", "Red Dead Redemption 2", "The Witcher 3: Wild Hunt", "Sekiro: Shadows Die Twice",
            "Dark Souls III", "Stardew Valley", "Celeste", "DOOM Eternal", "Resident Evil Village",
            "Monster Hunter: World", "Alan Wake 2", "Lies of P", "Dead Cells", "It Takes Two",
            "Hi-Fi RUSH", "Death Stranding", "Persona 5 Royal", "Sea of Thieves", "Starfield",
            "Assassin's Creed Valhalla", "Star Wars Jedi: Survivor", "Dave the Diver", "Hollow Knight: Silksong"
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
            var flagTally = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var comparedRows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using (views.BeginLease())
            {
                foreach (var game in MergeGames)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        await MergeCheckAsync(views, logger, game, outputDirectory, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger?.Warn(ex, $"[TrueGamingProbe] merge check '{game}' failed");
                    }
                }

                if (RunLanguageChecks)
                {
                    await ProbeLanguagesAsync(views, logger, outputDirectory, ct).ConfigureAwait(false);
                }

                if (!RunSiteChecks)
                {
                    logger?.Info($"[TrueGamingProbe] done; pages saved to {outputDirectory}");
                    return;
                }

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
                        await CompareAsync(views, logger, game, outputDirectory, flagTally, comparedRows, ct).ConfigureAwait(false);
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

            WriteFlagTally(logger, outputDirectory, flagTally, comparedRows);
            logger?.Info($"[TrueGamingProbe] done; pages saved to {outputDirectory}");
        }

        /// <summary>
        /// language.txt: for each of <see cref="LanguageUrls"/>, the final address, the page's and
        /// the browser's language, the site's cookies, the first titles and any language controls;
        /// for the header echo, the request headers the browser sent. Each page is saved beside it.
        /// </summary>
        private static async Task ProbeLanguagesAsync(OffscreenViewLeaseSource views, ILogger logger, string outputDirectory, CancellationToken ct)
        {
            var report = new StringBuilder();
            for (var i = 0; i < LanguageUrls.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var url = LanguageUrls[i];
                try
                {
                    var (html, text, info, address) = await views.WithNavigableViewAsync(async view =>
                    {
                        await view.NavigateAndWaitAsync(url, NavigationTimeoutMs).ConfigureAwait(false);
                        var source = await view.GetPageSourceAsync().ConfigureAwait(false);
                        var pageText = await view.GetPageTextAsync().ConfigureAwait(false);
                        var eval = await view.EvaluateScriptAsync(LanguageScript).ConfigureAwait(false);
                        return (source ?? string.Empty, pageText ?? string.Empty, eval?.Success == true ? Convert.ToString(eval.Result) : "script failed", view.GetCurrentAddress());
                    }, ct).ConfigureAwait(false);

                    File.WriteAllText(Path.Combine(outputDirectory, $"language-{i:00}-{FileStem(url)}.html"), html, Encoding.UTF8);
                    report.AppendLine(url);
                    report.AppendLine($"  -> {address}");
                    if (url.Contains("httpbin.org"))
                    {
                        report.AppendLine("  " + text.Replace("\r", "").Replace("\n", " "));
                    }
                    else
                    {
                        report.AppendLine("  " + info);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    report.AppendLine(url);
                    report.AppendLine($"  failed: {ex.GetType().Name}: {ex.Message}");
                }
            }

            File.WriteAllText(Path.Combine(outputDirectory, "language.txt"), report.ToString(), Encoding.UTF8);
            logger?.Info($"[TrueGamingProbe] language {report.ToString().Replace("\r", "").Replace("\n", " || ")}");
        }

        /// <summary>
        /// compare-flags.txt: for each flag, how many name-matched rows had it on one site's page
        /// and not the other's, per pair of sites, out of the rows compared for that pair.
        /// </summary>
        private static void WriteFlagTally(ILogger logger, string outputDirectory, Dictionary<string, int> flagTally, Dictionary<string, int> comparedRows)
        {
            var report = new StringBuilder();
            report.AppendLine("rows compared (both pages flagged): " + string.Join(", ", comparedRows.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}")));
            foreach (var flag in flagTally.Keys.Select(key => key.Split('|')[0]).Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderByDescending(flag => flagTally.Where(pair => pair.Key.StartsWith(flag + "|", StringComparison.OrdinalIgnoreCase)).Sum(pair => pair.Value)))
            {
                var byPair = flagTally.Where(pair => pair.Key.StartsWith(flag + "|", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(pair => pair.Key)
                    .Select(pair => $"{pair.Key.Split('|')[1]}:{pair.Value}");
                report.AppendLine($"  {flag}: {string.Join(" ", byPair)}");
            }

            File.WriteAllText(Path.Combine(outputDirectory, "compare-flags.txt"), report.ToString(), Encoding.UTF8);
            logger?.Info($"[TrueGamingProbe] flag tally {report.ToString().Replace("\r", "").Replace("\n", " || ")}");
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
        private static async Task CompareAsync(
            OffscreenViewLeaseSource views,
            ILogger logger,
            string game,
            string outputDirectory,
            Dictionary<string, int> flagTally,
            Dictionary<string, int> comparedRows,
            CancellationToken ct)
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
                    CompareRows(report, found[i].Site.Prefix, found[i].Page, found[j].Site.Prefix, found[j].Page, flagTally, comparedRows);
                }
            }

            var file = Path.Combine(outputDirectory, $"compare-{TrueGamingLinks.ToSlug(game)}.txt");
            File.WriteAllText(file, report.ToString(), Encoding.UTF8);
            logger?.Info($"[TrueGamingProbe] compare {report.ToString().Replace("\r", "").Replace("\n", " || ")}");
        }

        /// <summary>
        /// merge-&lt;slug&gt;.txt: each site's page for <paramref name="slug"/> (the name, then its
        /// edition variants) and its rows, the rows <see cref="TrueGamingPageSet.Combine"/> makes of
        /// them with their points, and every row listed by one site only, with the rows of other
        /// sites that share its description.
        /// </summary>
        private static async Task MergeCheckAsync(
            OffscreenViewLeaseSource views,
            ILogger logger,
            string slug,
            string outputDirectory,
            CancellationToken ct)
        {
            var report = new StringBuilder();
            report.AppendLine(slug);
            var found = new List<TrueGamingFoundPage>();
            foreach (var site in TrueGamingSite.LookupOrder)
            {
                var hit = false;
                foreach (var candidate in TrueGamingLinks.SlugsFor(site, slug))
                {
                    ct.ThrowIfCancellationRequested();
                    var url = site.ListUrl(candidate);
                    var watch = Stopwatch.StartNew();
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
                    report.AppendLine(
                        $"  {site.Prefix}:{candidate} -> {address} | {watch.ElapsedMilliseconds} ms, bytes={html.Length}, " +
                        $"challenge={IsChallenge(html)}, rows={page.Achievements.Count}");
                    if (page.Achievements.Count == 0)
                    {
                        continue;
                    }

                    File.WriteAllText(Path.Combine(outputDirectory, $"merge-{site.Prefix}-{candidate}.html"), html, Encoding.UTF8);
                    found.Add(new TrueGamingFoundPage(site, candidate, page));
                    hit = true;
                    break;
                }

                if (!hit)
                {
                    report.AppendLine($"  {site.Prefix}: no page");
                }
            }

            foreach (var entry in found)
            {
                report.AppendLine($"[{entry.Site.Prefix}] {entry.Slug}");
                foreach (var row in entry.Page.Achievements)
                {
                    report.AppendLine(
                        $"    {row.Id} | {row.Name} | points={row.Points?.ToString() ?? "-"} | trophy={row.TrophyType ?? "-"} | " +
                        $"dlc={row.Dlc ?? "-"} | desc={DescriptionKey(row.Description)}");
                }
            }

            var combined = TrueGamingPageSet.Combine(found, "Base");
            report.AppendLine($"[combined] rows={combined.Count}, withPoints={combined.Count(row => row.Points.HasValue)}");
            foreach (var row in combined)
            {
                report.AppendLine($"    {row.ApiName} | {row.DisplayName} | points={row.Points?.ToString() ?? "-"} | trophy={row.TrophyType ?? "-"} | category={row.Category}");
            }

            // A row only one site lists is a name the sites disagree on, or an achievement only
            // that site has; a shared description tells the two apart.
            var listings = found
                .SelectMany(entry => entry.Page.Achievements.Select(row => (entry.Site, Row: row)))
                .ToList();
            var sitesByName = listings
                .GroupBy(listing => TrueGamingPageSet.NameKey(listing.Row.Name))
                .ToDictionary(group => group.Key, group => group.Select(listing => listing.Site).Distinct().Count());
            report.AppendLine("[single-site rows]");
            foreach (var (site, row) in listings.Where(listing => sitesByName[TrueGamingPageSet.NameKey(listing.Row.Name)] == 1))
            {
                var key = DescriptionKey(row.Description);
                var sameDescription = listings
                    .Where(other => other.Site != site && key.Length > 0 && DescriptionKey(other.Row.Description) == key)
                    .Select(other => $"{other.Site.Prefix}:{other.Row.Name} (dlc={other.Row.Dlc ?? "-"}, points={other.Row.Points?.ToString() ?? "-"})")
                    .ToList();
                var duplicates = listings.Count(other => other.Site == site && DescriptionKey(other.Row.Description) == key);
                report.AppendLine(
                    $"    {site.Prefix}:{row.Name} (dlc={row.Dlc ?? "-"}) | descriptionOnItsPage={duplicates} | " +
                    $"sameDescription=[{string.Join("; ", sameDescription)}]");
            }

            File.WriteAllText(Path.Combine(outputDirectory, $"merge-{slug}.txt"), report.ToString(), Encoding.UTF8);
            logger?.Info($"[TrueGamingProbe] merge {report.ToString().Replace("\r", "").Replace("\n", " || ")}");
        }

        private static string DescriptionKey(string description) => TrueGamingPageSet.NameKey(description);

        private static void CompareRows(
            StringBuilder report,
            string leftName,
            TrueGamingPage left,
            string rightName,
            TrueGamingPage right,
            Dictionary<string, int> flagTally,
            Dictionary<string, int> comparedRows)
        {
            // A page with no flags on any row has nothing to disagree with; it is left out of the tally.
            var tally = left.Achievements.Any(row => row.Flags.Count > 0) && right.Achievements.Any(row => row.Flags.Count > 0);
            var pair = $"{leftName}-{rightName}";
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
                if (tally)
                {
                    comparedRows[pair] = (comparedRows.TryGetValue(pair, out var rows) ? rows : 0) + 1;
                    foreach (var flag in row.Flags.Concat(other.Flags).Distinct(StringComparer.OrdinalIgnoreCase)
                                 .Where(flag => row.Flags.Contains(flag, StringComparer.OrdinalIgnoreCase) != other.Flags.Contains(flag, StringComparer.OrdinalIgnoreCase)))
                    {
                        var key = flag + "|" + pair;
                        flagTally[key] = (flagTally.TryGetValue(key, out var count) ? count : 0) + 1;
                    }
                }

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
