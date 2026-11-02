using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Metadata.Exophase;
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
    /// Developer-only check of how the TrueGaming and SteamHunters sources link collections and
    /// games with DLC. Each case is a game's achievements read from a source that has them without
    /// the game in the library (a Steam app through SteamHunters, or an Exophase page found by
    /// name), handed to the source under test as the overlay would. Reports the link the source
    /// finds, how much its lists match, and, for each sizeable category, what the source finds
    /// when asked for that category by its own name. Writes one report per case and source.
    /// </summary>
    internal static class MetadataSourceProbe
    {
        /// <summary>Hardcoded gate for the hidden "Probe collections (TrueGaming, SteamHunters)" menu item. Off in shipped builds.</summary>
        internal static readonly bool Enabled = false;

        private const int CategoryMinimum = 5;

        /// <summary>
        /// A case: where the game's achievements are read from ("steam:&lt;appid&gt;", or
        /// "exophase:&lt;branch&gt;" found by the name), the platform it stands for, and the
        /// sources to ask.
        /// </summary>
        private static readonly (string Name, string From, string Servicing, string[] Sources)[] Cases =
        {
            // Steam games to TrueGaming: a collection is one TrueSteamAchievements page, but may
            // be several on TrueAchievements or TrueTrophies.
            ("Mass Effect Legendary Edition", "steam:1328670", "Steam", new[] { "TrueGaming" }),
            ("KINGDOM HEARTS -HD 1.5+2.5 ReMIX-", "steam:2552430", "Steam", new[] { "TrueGaming" }),
            ("Halo: The Master Chief Collection", "steam:976730", "Steam", new[] { "TrueGaming" }),
            ("Spyro Reignited Trilogy", "steam:996580", "Steam", new[] { "TrueGaming" }),
            ("Monster Hunter: World", "steam:582010", "Steam", new[] { "TrueGaming" }),
            ("Tomb Raider I-III Remastered", "steam:2478970", "Steam", new[] { "TrueGaming" }),
            ("ELDEN RING", "steam:1245620", "Steam", new[] { "TrueGaming" }),
            ("The Binding of Isaac: Rebirth", "steam:250900", "Steam", new[] { "TrueGaming" }),

            // PlayStation and Xbox games to both: one console list may be one Steam app or several.
            ("Legendary Edition: Mass Effect 2", "exophase:PlayStation", "PSN", new[] { "TrueGaming", "SteamHunters" }),
            ("KINGDOM HEARTS HD 1.5+2.5 ReMIX", "exophase:PlayStation", "PSN", new[] { "TrueGaming", "SteamHunters" }),
            ("Spyro the Dragon", "exophase:PlayStation", "PSN", new[] { "TrueGaming", "SteamHunters" }),
            ("Monster Hunter: World", "exophase:PlayStation", "PSN", new[] { "TrueGaming", "SteamHunters" }),
            ("Crash Bandicoot N. Sane Trilogy", "exophase:Xbox", "Xbox", new[] { "TrueGaming", "SteamHunters" }),
            ("Halo: The Master Chief Collection", "exophase:Xbox", "Xbox", new[] { "TrueGaming", "SteamHunters" }),
            ("The Orange Box", "exophase:Xbox", "Xbox", new[] { "TrueGaming", "SteamHunters" }),
            ("Batman: Return to Arkham", "exophase:Xbox", "Xbox", new[] { "TrueGaming", "SteamHunters" }),
            ("BioShock: The Collection", "exophase:Xbox", "Xbox", new[] { "TrueGaming", "SteamHunters" }),
            ("Elden Ring", "exophase:PlayStation", "PSN", new[] { "TrueGaming", "SteamHunters" }),
            ("The Binding of Isaac: Rebirth", "exophase:PlayStation", "PSN", new[] { "TrueGaming", "SteamHunters" }),
            ("The Binding of Isaac: Rebirth", "exophase:Xbox", "Xbox", new[] { "TrueGaming", "SteamHunters" })
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
            var pages = new ExophasePageSource(api, settings, logger);
            var exophase = new[] { ExophaseMetadataBranch.PlayStation, ExophaseMetadataBranch.Xbox }
                .ToDictionary(branch => branch.Key, branch => new ExophaseMetadataProvider(branch, pages, logger), StringComparer.OrdinalIgnoreCase);

            var open = new List<IMetadataProvider>();
            foreach (var key in new[] { SteamHuntersMetadataProvider.MetadataKey, "TrueGaming" })
            {
                if (byKey.TryGetValue(key, out var provider) && await provider.BeginRunAsync(ct).ConfigureAwait(false))
                {
                    open.Add(provider);
                }
            }

            var exophaseOpen = await exophase[ExophaseMetadataBranch.PlayStation.Key].BeginRunAsync(ct).ConfigureAwait(false);
            try
            {
                foreach (var (name, from, servicing, sources) in Cases)
                {
                    ct.ThrowIfCancellationRequested();
                    List<AchievementDetail> rows;
                    string origin;
                    try
                    {
                        (rows, origin) = await LoadTargetsAsync(name, from, servicing, byKey, exophase, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        rows = new List<AchievementDetail>();
                        origin = $"failed: {ex.GetType().Name}: {ex.Message}";
                    }

                    // What Exophase finds the game under on another platform, shared with the
                    // sources under test as the overlay shares one source's names with the others.
                    string sharedLabel = null;
                    if (exophaseOpen && rows.Count > 0)
                    {
                        var other = servicing == "PSN" ? ExophaseMetadataBranch.Xbox.Key : ExophaseMetadataBranch.PlayStation.Key;
                        try
                        {
                            sharedLabel = (await exophase[other].ResolveLinkAsync(Request(name, servicing, rows), ct).ConfigureAwait(false))?.Label;
                        }
                        catch (Exception ex) when (!(ex is OperationCanceledException))
                        {
                            sharedLabel = null;
                        }
                    }

                    foreach (var sourceKey in sources)
                    {
                        var report = new StringBuilder();
                        report.AppendLine($"{name} ({servicing}, {origin}): {rows.Count} achievements -> {sourceKey}");
                        report.AppendLine("shared by the sources before: " + (sharedLabel ?? "nothing").Replace("\t", " => ").Replace("\n", "; "));
                        var provider = open.FirstOrDefault(entry => string.Equals(entry.Key, sourceKey, StringComparison.OrdinalIgnoreCase));
                        try
                        {
                            if (provider == null)
                            {
                                report.AppendLine("  source unavailable");
                            }
                            else if (rows.Count > 0)
                            {
                                // Each source's found names go to the sources after it, as the overlay shares them.
                                var found = await ProbeSourceAsync(provider, name, servicing, rows, sharedLabel, report, ct).ConfigureAwait(false);
                                sharedLabel = MetadataFoundNames.Format(MetadataFoundNames.Parse(sharedLabel).Concat(MetadataFoundNames.Parse(found)));
                            }
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            report.AppendLine($"failed: {ex.GetType().Name}: {ex.Message}");
                        }

                        File.WriteAllText(Path.Combine(outputDirectory, FileStem($"{name} {servicing} {sourceKey}") + ".txt"), report.ToString(), Encoding.UTF8);
                        logger?.Info($"[SourceProbe] {report.ToString().Replace("\r", "").Replace("\n", " || ")}");
                    }
                }
            }
            finally
            {
                foreach (var provider in open)
                {
                    provider.EndRun();
                }

                if (exophaseOpen)
                {
                    exophase[ExophaseMetadataBranch.PlayStation.Key].EndRun();
                }
            }

            logger?.Info($"[SourceProbe] done; reports saved to {outputDirectory}");
        }

        /// <summary>The case's achievements, as the platform it stands for would give them, and where they came from.</summary>
        private static async Task<(List<AchievementDetail> Rows, string Origin)> LoadTargetsAsync(
            string name,
            string from,
            string servicing,
            IReadOnlyDictionary<string, IMetadataProvider> byKey,
            IReadOnlyDictionary<string, ExophaseMetadataProvider> exophase,
            CancellationToken ct)
        {
            var parts = from.Split(':');
            if (parts[0] == "steam")
            {
                if (!byKey.TryGetValue(SteamHuntersMetadataProvider.MetadataKey, out var steamHunters))
                {
                    return (new List<AchievementDetail>(), "SteamHunters unavailable");
                }

                var set = await steamHunters.FetchAsync(Request(name, "Steam", null), parts[1], ct).ConfigureAwait(false);
                return (set?.Achievements?.Where(row => row != null).ToList() ?? new List<AchievementDetail>(), $"Steam app {parts[1]}");
            }

            var branch = exophase[parts[1]];
            var link = await branch.ResolveLinkAsync(Request(name, servicing, null), ct).ConfigureAwait(false);
            if (link?.Id == null)
            {
                return (new List<AchievementDetail>(), "no Exophase page");
            }

            var page = await branch.FetchAsync(Request(name, servicing, null), link.Id, ct).ConfigureAwait(false);
            return (page?.Achievements?.Where(row => row != null).ToList() ?? new List<AchievementDetail>(), $"Exophase {link.Id}");
        }

        /// <summary>The source's lookups for the case; returns the names it found the whole game under.</summary>
        private static async Task<string> ProbeSourceAsync(
            IMetadataProvider provider,
            string name,
            string servicing,
            List<AchievementDetail> rows,
            string sharedLabel,
            StringBuilder report,
            CancellationToken ct)
        {
            report.AppendLine($"sample: {string.Join("; ", rows.Take(3).Select(row => row.DisplayName))}");
            string foundWithShared = null;
            if (sharedLabel != null)
            {
                report.AppendLine("== whole game, with the names the sources before found ==");
                foundWithShared = await ResolveAndMatchAsync(provider, name, servicing, rows, report, ct, sharedLabel).ConfigureAwait(false);
            }

            var categories = rows
                .Where(row => !string.IsNullOrWhiteSpace(row.Category))
                .GroupBy(row => row.Category.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group => (Name: group.Key, Kind: group.Select(row => row.CategoryKind).FirstOrDefault(kind => !string.IsNullOrWhiteSpace(kind)), Rows: group.ToList()))
                .ToList();
            report.AppendLine("categories: " + string.Join("; ", categories.Select(category => $"{category.Name} [{category.Kind ?? "no kind"}] {category.Rows.Count}")));

            report.AppendLine("== whole game, on its own ==");
            var found = await ResolveAndMatchAsync(provider, name, servicing, rows, report, ct).ConfigureAwait(false);

            var sizeable = categories.Where(category => category.Rows.Count >= CategoryMinimum).ToList();
            if (sizeable.Count >= 2)
            {
                foreach (var category in sizeable)
                {
                    report.AppendLine($"== category '{category.Name}' ({category.Rows.Count}) by its own name ==");
                    await ResolveAndMatchAsync(provider, category.Name, servicing, category.Rows, report, ct).ConfigureAwait(false);
                }
            }

            return foundWithShared ?? found;
        }

        /// <summary>
        /// The source's link for a game of this name and these achievements, its lists, and how much
        /// they match. Returns the names the source found it under (its link label), or null.
        /// </summary>
        private static async Task<string> ResolveAndMatchAsync(
            IMetadataProvider provider,
            string name,
            string servicing,
            List<AchievementDetail> rows,
            StringBuilder report,
            CancellationToken ct,
            string sharedLabel = null)
        {
            var request = Request(name, servicing, rows);
            request.WantedFields = provider.SupportedFields;
            var shared = MetadataFoundNames.Parse(sharedLabel);
            request.AlternateNames = shared.Where(found => found.Category == null).Select(found => found.Title).ToList();
            request.CategoryNames = shared
                .Where(found => found.Category != null)
                .GroupBy(found => found.Category, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.Select(found => found.Title).ToList(), StringComparer.OrdinalIgnoreCase);
            var started = DateTime.UtcNow;
            var link = await provider.ResolveLinkAsync(request, ct).ConfigureAwait(false);
            if (link?.Id == null)
            {
                report.AppendLine($"  link: {(link?.IsUnavailable == true ? "unavailable, pages could not be read" : "none")} ({(DateTime.UtcNow - started).TotalSeconds:0.0} s)");
                if (link?.Trace != null)
                {
                    report.AppendLine($"  trace: {link.Trace}");
                }

                return null;
            }

            var set = await MetadataListFetch.FetchAsync(provider, request, link.Id, null, ct).ConfigureAwait(false);
            var sources = set?.Achievements?.ToList() ?? new List<AchievementDetail>();
            var pooled = CrossSourceAchievementMatcher.Match(rows, sources).Count;
            var byList = MetadataOverlayRun.MatchByList(rows, sources, set?.Lists, matchApiNames: false).Count;
            report.AppendLine($"  link: {link.Id}{(set?.ExtendedLinkId != null ? " (stored as " + set.ExtendedLinkId + ")" : string.Empty)} ({(DateTime.UtcNow - started).TotalSeconds:0.0} s, found by name: {link.FoundByName})");
            report.AppendLine($"  trace: {link.Trace ?? "none"}");
            var details = MetadataLinkIds.DescribeLink(provider as IMetadataLinkDetails, Guid.Empty, set?.ExtendedLinkId ?? link.Id);
            if (!string.IsNullOrWhiteSpace(details))
            {
                report.AppendLine("  pages: " + details.Replace(Environment.NewLine, "; "));
            }

            report.AppendLine(
                $"  rows {sources.Count}, matched pooled {pooled}/{rows.Count}, list by list {byList}/{rows.Count}; " +
                $"overlay accepts: {MetadataPageSearch.IsEnough(byList, rows.Count, sources.Count)}");
            if (sources.Count > 0 && byList < rows.Count)
            {
                var matched = new HashSet<AchievementDetail>(MetadataOverlayRun.MatchByList(rows, sources, set?.Lists, false).Select(match => match.Target));
                report.AppendLine($"  unmatched (first 6): {string.Join("; ", rows.Where(row => !matched.Contains(row)).Take(6).Select(row => row.DisplayName))}");
                report.AppendLine($"  source sample: {string.Join("; ", sources.Take(4).Select(row => row.DisplayName))}");
            }

            return link.Label;
        }

        // An empty game id keeps the sources from storing what they find for these made-up games.
        private static MetadataLinkRequest Request(string name, string servicing, List<AchievementDetail> rows) =>
            new MetadataLinkRequest(
                new Game(name) { Id = Guid.Empty },
                new GameAchievementData { GameName = name, ProviderKey = servicing, Achievements = rows ?? new List<AchievementDetail>() });

        private static string FileStem(string name)
        {
            var stem = Regex.Replace(name ?? "game", @"[^A-Za-z0-9]+", "-").Trim('-');
            return stem.Length == 0 ? "game" : stem;
        }
    }
}
