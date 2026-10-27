using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Metadata.Exophase;
using PlayniteAchievements.Providers.Metadata.TrueGaming;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Metadata;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Metadata
{
    /// <summary>
    /// Developer-only comparison of where tags and categories are best read from. Each case is a
    /// Steam app whose achievements (and SteamHunters categories) come from SteamHunters. For tags
    /// it reads the game's page on each TrueGaming site and reports what each site flags, what the
    /// other sites add, and how many page loads and tags each stop rule would give. For categories
    /// it compares the SteamHunters groups with the DLC lists of the TrueGaming pages and of the
    /// Exophase Xbox and PlayStation pages. Writes one report per case and a summary.
    /// </summary>
    internal static class TagsCategoriesProbe
    {
        /// <summary>Hardcoded gate for the hidden "Probe tags and categories" menu item. Off in shipped builds.</summary>
        internal static readonly bool Enabled = true;

        // (name, Steam appid): single games, games with DLC, and collections, on one platform or several.
        private static readonly (string Name, string AppId)[] Cases =
        {
            ("ELDEN RING", "1245620"),
            ("Fallout 4", "377160"),
            ("The Witcher 3: Wild Hunt", "292030"),
            ("Cyberpunk 2077", "1091500"),
            ("Monster Hunter: World", "582010"),
            ("Baldur's Gate 3", "1086940"),
            ("Hades", "1145360"),
            ("Hollow Knight", "367520"),
            ("Stardew Valley", "413150"),
            ("Celeste", "504230"),
            ("DOOM Eternal", "782330"),
            ("Dead Cells", "588650"),
            ("Sekiro: Shadows Die Twice", "814380"),
            ("Red Dead Redemption 2", "1174180"),
            ("Hogwarts Legacy", "990080"),
            ("Lies of P", "1627720"),
            ("Slay the Spire", "646570"),
            ("Vampire Survivors", "1794680"),
            ("The Binding of Isaac: Rebirth", "250900"),
            ("Mass Effect Legendary Edition", "1328670"),
            ("FINAL FANTASY X/X-2 HD Remaster", "359870"),
            ("Tomb Raider I-III Remastered", "2478970")
        };

        private static readonly TrueGamingSite[] Sites =
        {
            TrueGamingSite.TrueAchievements, TrueGamingSite.TrueTrophies, TrueGamingSite.TrueSteamAchievements
        };

        public static async Task RunAsync(
            IPlayniteAPI api,
            PlayniteAchievementsSettings settings,
            IReadOnlyList<IMetadataProvider> metadataProviders,
            ILogger logger,
            string outputDirectory,
            CancellationToken ct)
        {
            Directory.CreateDirectory(outputDirectory);
            var byKey = (metadataProviders ?? Array.Empty<IMetadataProvider>())
                .GroupBy(provider => provider.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            byKey.TryGetValue(SteamHuntersMetadataProvider.MetadataKey, out var steamHunters);
            var trueGaming = byKey.TryGetValue(TrueGamingMetadataProvider.MetadataKey, out var tg) ? tg as TrueGamingMetadataProvider : null;
            var pages = new ExophasePageSource(api, settings, logger);
            var exophase = new[] { ExophaseMetadataBranch.Xbox, ExophaseMetadataBranch.PlayStation }
                .Select(branch => new ExophaseMetadataProvider(branch, pages, logger))
                .ToList();

            var summary = new StringBuilder();
            var tagRows = new StringBuilder("game\tachievement\tsite\tpage\tflags\ttags\n");
            summary.AppendLine($"Tags and categories probe, {DateTime.Now:yyyy-MM-dd HH:mm:ss}.");
            summary.AppendLine("Tags: per site 'listed/flagged/pairs' of the game's achievements; union = distinct (achievement, tag) pairs over all sites.");
            summary.AppendLine("Rules: each gives loads, pairs kept / union. today = load until every achievement is flagged; listed = stop at the first page listing every achievement with any flags; own = own site (TSA) first, then listed.");
            summary.AppendLine();

            var open = new List<IMetadataProvider>();
            foreach (var provider in new IMetadataProvider[] { steamHunters, trueGaming, exophase[0] })
            {
                if (provider != null && await provider.BeginRunAsync(ct).ConfigureAwait(false))
                {
                    open.Add(provider);
                }
            }

            try
            {
                foreach (var (name, appId) in Cases)
                {
                    ct.ThrowIfCancellationRequested();
                    var report = new StringBuilder();
                    try
                    {
                        await ProbeCaseAsync(name, appId, steamHunters, trueGaming, exophase, report, summary, tagRows, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        report.AppendLine($"failed: {ex.GetType().Name}: {ex.Message}");
                        summary.AppendLine($"{name}: failed: {ex.GetType().Name}: {ex.Message}");
                    }

                    File.WriteAllText(Path.Combine(outputDirectory, FileStem(name) + ".txt"), report.ToString(), Encoding.UTF8);
                    File.WriteAllText(Path.Combine(outputDirectory, "summary.txt"), summary.ToString(), Encoding.UTF8);
                    File.WriteAllText(Path.Combine(outputDirectory, "tags.tsv"), tagRows.ToString(), Encoding.UTF8);
                    logger?.Info($"[TagsCategoriesProbe] {name} done");
                }
            }
            finally
            {
                foreach (var provider in open)
                {
                    provider.EndRun();
                }
            }

            summary.AppendLine("Done.");
            File.WriteAllText(Path.Combine(outputDirectory, "summary.txt"), summary.ToString(), Encoding.UTF8);
            logger?.Info($"[TagsCategoriesProbe] done; reports saved to {outputDirectory}");
        }

        private static async Task ProbeCaseAsync(
            string name,
            string appId,
            IMetadataProvider steamHunters,
            TrueGamingMetadataProvider trueGaming,
            IReadOnlyList<ExophaseMetadataProvider> exophase,
            StringBuilder report,
            StringBuilder summary,
            StringBuilder tagRows,
            CancellationToken ct)
        {
            var steamSet = steamHunters == null ? null : await steamHunters.FetchAsync(Request(name, null, MetadataField.Categories), appId, ct).ConfigureAwait(false);
            var targets = steamSet?.Achievements?.Where(row => row != null && !string.IsNullOrWhiteSpace(row.DisplayName)).ToList() ?? new List<AchievementDetail>();
            report.AppendLine($"{name} (Steam {appId}, SteamHunters title '{steamSet?.Title}'): {targets.Count} achievements");
            summary.Append($"{name} [{targets.Count}]: ");
            if (targets.Count == 0)
            {
                summary.AppendLine("no achievements from SteamHunters");
                return;
            }

            // Categories as SteamHunters gives them, before anything else is compared.
            var categorySources = new List<(string Source, IReadOnlyDictionary<AchievementDetail, string> ByTarget)>
            {
                ("SteamHunters", targets.ToDictionary(row => row, row => Label(row)))
            };

            // TrueGaming: the link the source finds, then each site's page under it.
            var siteRows = new List<(TrueGamingSite Site, string Slug, TrueGamingPage Page, List<AchievementMatch> Matches)>();
            if (trueGaming != null)
            {
                var request = Request(name, targets, MetadataField.Tags | MetadataField.Categories);
                var link = await trueGaming.ResolveLinkAsync(request, ct).ConfigureAwait(false);
                report.AppendLine($"TrueGaming link: {link?.Id ?? (link?.IsUnavailable == true ? "unavailable" : "none")} ({link?.Matched?.ToString() ?? "-"} matched)");
                foreach (var slug in MetadataLinkIds.Parse(link?.Id))
                {
                    foreach (var site in Sites)
                    {
                        var found = await FindSitePageAsync(trueGaming, site, slug, targets, ct).ConfigureAwait(false);
                        if (found.Page == null)
                        {
                            report.AppendLine($"  {site.Title}: no page for {slug}");
                            continue;
                        }

                        var rows = TrueGamingPageSet.CombineAll(new[] { new TrueGamingFoundPage(site, found.Slug, found.Page) }, name).Rows;
                        siteRows.Add((site, found.Slug, found.Page, CrossSourceAchievementMatcher.Match(targets, rows)));
                    }
                }
            }

            ReportTags(targets, siteRows, report, summary);
            AppendTagRows(name, siteRows, tagRows);

            if (siteRows.Count > 0)
            {
                var combined = TrueGamingPageSet.CombineAll(
                    siteRows.Select(entry => new TrueGamingFoundPage(entry.Site, entry.Slug, entry.Page)).ToList(), name).Rows;
                categorySources.Add(("TrueGaming", ByTarget(CrossSourceAchievementMatcher.Match(targets, combined))));
            }

            // Exophase: the Xbox and PlayStation pages the overlay would find for a Steam game.
            foreach (var branch in exophase)
            {
                var request = Request(name, targets, MetadataField.Categories | MetadataField.CategoryArt);
                var link = await branch.ResolveLinkAsync(request, ct).ConfigureAwait(false);
                if (link?.Id == null)
                {
                    report.AppendLine($"Exophase {branch.Key}: {(link?.IsUnavailable == true ? "unavailable" : "no page")}");
                    continue;
                }

                var set = await MetadataListFetch.FetchAsync(branch, request, link.Id, null, ct).ConfigureAwait(false);
                var rows = set?.Achievements?.ToList() ?? new List<AchievementDetail>();
                report.AppendLine($"Exophase {branch.Key}: {link.Id}, {rows.Count} rows");
                categorySources.Add(($"Exophase {branch.Key}", ByTarget(MetadataOverlayRun.MatchByList(targets, rows, set?.Lists, matchApiNames: false))));
            }

            ReportCategories(targets, categorySources, report, summary);
            summary.AppendLine();
        }

        /// <summary>
        /// The site's page for a link name: the name, then its edition variants, keeping the first
        /// whose achievements are the game's.
        /// </summary>
        private static async Task<(string Slug, TrueGamingPage Page)> FindSitePageAsync(
            TrueGamingMetadataProvider trueGaming,
            TrueGamingSite site,
            string slug,
            List<AchievementDetail> targets,
            CancellationToken ct)
        {
            var names = targets.Select(row => row.DisplayName).ToList();
            foreach (var candidate in TrueGamingLinks.SlugsToTry(site, site, TrueGamingLinks.BaseSlug(slug), userSet: true))
            {
                var page = await trueGaming.LoadPageAsync(site, candidate, ct).ConfigureAwait(false);
                if (page.Achievements.Count > 0 && TrueGamingPageSet.IsSameGame(page, names))
                {
                    return (candidate, page);
                }
            }

            return (null, null);
        }

        private static void ReportTags(
            List<AchievementDetail> targets,
            List<(TrueGamingSite Site, string Slug, TrueGamingPage Page, List<AchievementMatch> Matches)> siteRows,
            StringBuilder report,
            StringBuilder summary)
        {
            var bySite = new Dictionary<TrueGamingSite, Dictionary<AchievementDetail, HashSet<string>>>();
            foreach (var entry in siteRows)
            {
                if (!bySite.TryGetValue(entry.Site, out var tags))
                {
                    tags = new Dictionary<AchievementDetail, HashSet<string>>();
                    bySite[entry.Site] = tags;
                }

                foreach (var match in entry.Matches)
                {
                    if (!tags.TryGetValue(match.Target, out var set))
                    {
                        set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        tags[match.Target] = set;
                    }

                    set.UnionWith(AchievementTagHelper.Parse(match.Source.Tags));
                }
            }

            var union = new HashSet<(AchievementDetail, string)>(
                bySite.Values.SelectMany(tags => tags.SelectMany(pair => pair.Value.Select(tag => (pair.Key, tag)))));
            report.AppendLine("== tags ==");
            summary.Append("tags ");
            foreach (var site in Sites)
            {
                if (!bySite.TryGetValue(site, out var tags))
                {
                    summary.Append($"{site.Prefix}=- ");
                    continue;
                }

                var listed = tags.Count;
                var flagged = tags.Count(pair => pair.Value.Count > 0);
                var pairs = tags.Sum(pair => pair.Value.Count);
                var flags = siteRows.First(entry => entry.Site == site).Page.Achievements.SelectMany(row => row.Flags).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                report.AppendLine($"  {site.Title} ({siteRows.First(entry => entry.Site == site).Slug}): listed {listed}/{targets.Count}, flagged {flagged}, pairs {pairs}/{union.Count}; flags on page: {string.Join(", ", flags)}");
                summary.Append($"{site.Prefix}={listed}/{flagged}/{pairs} ");
            }

            summary.Append($"union={union.Count}; ");

            // What each later site adds over the earlier ones, on achievements an earlier site lists.
            var seen = new Dictionary<AchievementDetail, HashSet<string>>();
            foreach (var site in Sites.Where(bySite.ContainsKey))
            {
                var adds = bySite[site]
                    .Where(pair => seen.ContainsKey(pair.Key))
                    .SelectMany(pair => pair.Value.Where(tag => !seen[pair.Key].Contains(tag)).Select(tag => $"{pair.Key.DisplayName}: {tag}"))
                    .ToList();
                if (seen.Count > 0)
                {
                    report.AppendLine($"  {site.Title} adds on already-listed achievements ({adds.Count}): {string.Join("; ", adds.Take(12))}");
                }

                foreach (var pair in bySite[site])
                {
                    if (!seen.TryGetValue(pair.Key, out var set))
                    {
                        seen[pair.Key] = new HashSet<string>(pair.Value, StringComparer.OrdinalIgnoreCase);
                    }
                    else
                    {
                        set.UnionWith(pair.Value);
                    }
                }
            }

            foreach (var (rule, order, stop) in new (string, TrueGamingSite[], Func<Dictionary<AchievementDetail, HashSet<string>>, bool>)[]
            {
                ("today", Sites, kept => targets.All(target => kept.TryGetValue(target, out var set) && set.Count > 0)),
                ("listed", Sites, kept => targets.All(kept.ContainsKey) && kept.Values.Any(set => set.Count > 0)),
                ("own", new[] { TrueGamingSite.TrueSteamAchievements, TrueGamingSite.TrueAchievements, TrueGamingSite.TrueTrophies },
                    kept => targets.All(kept.ContainsKey) && kept.Values.Any(set => set.Count > 0))
            })
            {
                // As the provider reads them: each achievement keeps the tags of the first page that flags it.
                var kept = new Dictionary<AchievementDetail, HashSet<string>>();
                var loads = 0;
                foreach (var site in order.Where(bySite.ContainsKey))
                {
                    loads++;
                    foreach (var pair in bySite[site])
                    {
                        if (!kept.TryGetValue(pair.Key, out var set) || set.Count == 0)
                        {
                            kept[pair.Key] = pair.Value;
                        }
                    }

                    if (stop(kept))
                    {
                        break;
                    }
                }

                var pairsKept = kept.Sum(pair => pair.Value.Count);
                report.AppendLine($"  rule {rule}: {loads} loads, {pairsKept}/{union.Count} pairs");
                summary.Append($"{rule}={loads}L {pairsKept}/{union.Count} ");
            }

            report.AppendLine();
        }

        /// <summary>
        /// One tags.tsv line per achievement each site lists: the page, the site's own flag names
        /// and the tag keys they give, so tags can be compared flag by flag across sites.
        /// </summary>
        private static void AppendTagRows(
            string game,
            List<(TrueGamingSite Site, string Slug, TrueGamingPage Page, List<AchievementMatch> Matches)> siteRows,
            StringBuilder tagRows)
        {
            foreach (var entry in siteRows)
            {
                var byId = entry.Page.Achievements
                    .Where(row => !string.IsNullOrWhiteSpace(row.Id))
                    .GroupBy(row => row.Id, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                foreach (var match in entry.Matches)
                {
                    byId.TryGetValue(match.Source.ApiName ?? string.Empty, out var row);
                    tagRows.Append(game).Append('\t')
                        .Append(match.Target.DisplayName).Append('\t')
                        .Append(entry.Site.Prefix).Append('\t')
                        .Append(entry.Slug).Append('\t')
                        .Append(string.Join(",", row?.Flags ?? new List<string>())).Append('\t')
                        .Append(string.Join(",", AchievementTagHelper.Parse(match.Source.Tags)))
                        .Append('\n');
                }
            }
        }

        private static void ReportCategories(            List<AchievementDetail> targets,
            List<(string Source, IReadOnlyDictionary<AchievementDetail, string> ByTarget)> sources,
            StringBuilder report,
            StringBuilder summary)
        {
            report.AppendLine("== categories ==");
            summary.Append("| categories ");
            foreach (var (source, byTarget) in sources)
            {
                var groups = byTarget.Values.GroupBy(label => label, StringComparer.OrdinalIgnoreCase).ToList();
                report.AppendLine($"  {source}: covers {byTarget.Count}/{targets.Count}, {groups.Count} categories: {string.Join("; ", groups.Select(group => $"{group.Key} {group.Count()}"))}");
                summary.Append($"{source}={byTarget.Count}c/{groups.Count}g ");
            }

            // How far each pair agrees on which achievements belong together, over the achievements
            // both cover: each of the first's categories counts its largest share in one of the second's.
            for (var i = 0; i < sources.Count; i++)
            {
                for (var j = i + 1; j < sources.Count; j++)
                {
                    var shared = sources[i].ByTarget.Keys.Where(sources[j].ByTarget.ContainsKey).ToList();
                    if (shared.Count == 0)
                    {
                        continue;
                    }

                    var agree = shared
                        .GroupBy(target => sources[i].ByTarget[target], StringComparer.OrdinalIgnoreCase)
                        .Sum(group => group.GroupBy(target => sources[j].ByTarget[target], StringComparer.OrdinalIgnoreCase).Max(inner => inner.Count()));
                    var disagree = shared
                        .Where(target => Kind(sources[i].ByTarget[target]) != Kind(sources[j].ByTarget[target]))
                        .Select(target => $"{target.DisplayName} ({sources[i].ByTarget[target]} / {sources[j].ByTarget[target]})")
                        .Take(8)
                        .ToList();
                    report.AppendLine($"  {sources[i].Source} vs {sources[j].Source}: {agree}/{shared.Count} grouped alike; base/DLC differs on: {string.Join("; ", disagree)}");
                    summary.Append($"{Short(sources[i].Source)}~{Short(sources[j].Source)}={agree}/{shared.Count} ");
                }
            }

            report.AppendLine();
        }

        private static IReadOnlyDictionary<AchievementDetail, string> ByTarget(IEnumerable<AchievementMatch> matches)
        {
            var byTarget = new Dictionary<AchievementDetail, string>();
            foreach (var match in matches)
            {
                if (!byTarget.ContainsKey(match.Target))
                {
                    byTarget[match.Target] = Label(match.Source);
                }
            }

            return byTarget;
        }

        // The base game's rows share one label, whatever each source calls the game.
        private static string Label(AchievementDetail row) =>
            string.Equals(row?.CategoryKind, CategoryKindHelper.Dlc, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(row?.Category)
                ? row.Category.Trim()
                : string.IsNullOrWhiteSpace(row?.Category) ? "(base)" : "(base) " + row.Category.Trim();

        private static bool Kind(string label) => !label.StartsWith("(base)", StringComparison.Ordinal);

        private static string Short(string source) =>
            source == "SteamHunters" ? "sh" : source == "TrueGaming" ? "tg" : source.EndsWith("Xbox") ? "exX" : "exP";

        // An empty game id keeps the sources from storing what they find for these made-up games.
        private static MetadataLinkRequest Request(string name, List<AchievementDetail> rows, MetadataField wanted) =>
            new MetadataLinkRequest(
                new Game(name) { Id = Guid.Empty },
                new GameAchievementData { GameName = name, ProviderKey = "Steam", Achievements = rows ?? new List<AchievementDetail>() })
            {
                WantedFields = wanted
            };

        private static string FileStem(string name)
        {
            var stem = Regex.Replace(name ?? "game", @"[^A-Za-z0-9]+", "-").Trim('-');
            return stem.Length == 0 ? "game" : stem;
        }
    }
}
