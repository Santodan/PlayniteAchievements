using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Exophase;
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

namespace PlayniteAchievements.Providers.Metadata.Exophase
{
    /// <summary>
    /// Developer-only check of how a collection (one game here, several games on Exophase) would
    /// link to the PlayStation branch. Probes a fixed list of Steam collections, read through the
    /// SteamHunters source (Steam schema rows grouped by SteamHunters), so the games need not be
    /// in the library, plus any games selected in the library. For the whole title and each base
    /// category it logs Exophase's raw search results, the page the branch's lookup picks, and how
    /// many of the category's achievements each PlayStation result's page matches. Writes one
    /// report per game under the output folder.
    /// </summary>
    internal static class ExophaseCollectionProbe
    {
        /// <summary>Hardcoded gate for the hidden "Probe collection" menu item. Off in shipped builds.</summary>
        internal static readonly bool Enabled = false;

        // Steam apps that may be several lists on the borrowed platform, and controls: one-list
        // games with and without DLC. Each runs against one Exophase branch.
        private static readonly (int AppId, string Name, string Branch)[] SteamCollections =
        {
            (1328670, "Mass Effect Legendary Edition", "PlayStation"),
            (359870, "FINAL FANTASY X/X-2 HD Remaster", "PlayStation"),
            (1659420, "UNCHARTED: Legacy of Thieves Collection", "PlayStation"),
            (2552430, "KINGDOM HEARTS -HD 1.5+2.5 ReMIX-", "PlayStation"),
            (2552440, "KINGDOM HEARTS HD 2.8 Final Chapter Prologue", "PlayStation"),
            (996580, "Spyro Reignited Trilogy", "PlayStation"),
            (731490, "Crash Bandicoot N. Sane Trilogy", "PlayStation"),
            (1265920, "Life is Strange Remastered Collection", "PlayStation"),
            (758330, "Shenmue I & II", "PlayStation"),
            (631510, "Devil May Cry HD Collection", "PlayStation"),
            (2478970, "Tomb Raider I-III Remastered", "PlayStation"),
            (1245620, "ELDEN RING", "PlayStation"),
            (250900, "The Binding of Isaac: Rebirth", "PlayStation"),
            (976730, "Halo: The Master Chief Collection", "Xbox"),
            (1328670, "Mass Effect Legendary Edition", "Xbox"),

            // More collections: one app on Steam, maybe one list or several elsewhere.
            (1794960, "Sonic Origins", "PlayStation"),
            (363440, "Mega Man Legacy Collection", "PlayStation"),
            (787480, "Phoenix Wright: Ace Attorney Trilogy", "PlayStation"),
            (1552550, "Castlevania Advance Collection", "PlayStation"),
            (1462040, "FINAL FANTASY VII REMAKE INTERGRADE", "PlayStation"),
            (2552450, "KINGDOM HEARTS III + Re Mind (DLC)", "PlayStation"),

            // Controls: one game, with DLC sections, renamed releases and large lists.
            (292030, "The Witcher 3: Wild Hunt", "PlayStation"),
            (374320, "DARK SOULS III", "PlayStation"),
            (582010, "Monster Hunter: World", "PlayStation"),
            (1817070, "Marvel's Spider-Man Remastered", "PlayStation"),
            (208650, "Batman: Arkham Knight", "PlayStation"),
            (1145360, "Hades", "PlayStation"),
            (1687950, "Persona 5 Royal", "PlayStation"),
            (638970, "Yakuza 0", "PlayStation"),
            (409710, "BioShock Remastered", "PlayStation"),
            (2050650, "Resident Evil 4", "PlayStation"),
            (524220, "NieR:Automata", "PlayStation"),
            (377160, "Fallout 4", "Xbox"),
            (1097840, "Gears 5", "Xbox"),
            (1057090, "Ori and the Will of the Wisps", "Xbox"),
            (607080, "Psychonauts 2", "Xbox"),
            (1551360, "Forza Horizon 5", "Xbox"),

            // Xbox cases after their PlayStation ones: the Xbox branch follows the PlayStation link.
            (1245620, "ELDEN RING", "Xbox"),
            (250900, "The Binding of Isaac: Rebirth", "Xbox"),
            (292030, "The Witcher 3: Wild Hunt", "Xbox"),
            (1145360, "Hades", "Xbox"),
            (2552430, "KINGDOM HEARTS -HD 1.5+2.5 ReMIX-", "Xbox")
        };

        // The PlayStation platforms whose pages the survey scores for the PlayStation branch.
        private static readonly HashSet<string> PlayStationPlatforms =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ps5", "ps4", "ps3", "psvita" };

        public static async Task RunAsync(
            IPlayniteAPI api,
            PlayniteAchievementsSettings settings,
            IReadOnlyList<IMetadataProvider> metadataProviders,
            AchievementDataService data,
            IEnumerable<Game> selected,
            ILogger logger,
            string outputDirectory,
            CancellationToken ct)
        {
            Directory.CreateDirectory(outputDirectory);
            var pages = new ExophasePageSource(api, settings, logger);
            var providers = new[] { ExophaseMetadataBranch.PlayStation, ExophaseMetadataBranch.Xbox }
                .ToDictionary(branch => branch.Key, branch => new ExophaseMetadataProvider(branch, pages, logger), StringComparer.OrdinalIgnoreCase);
            var playStation = providers[ExophaseMetadataBranch.PlayStation.Key];
            var client = new ExophaseApiClient(api, logger, null);
            var steamHunters = metadataProviders?.FirstOrDefault(source =>
                string.Equals(source.Key, SteamHuntersMetadataProvider.MetadataKey, StringComparison.OrdinalIgnoreCase));
            if (!await playStation.BeginRunAsync(ct).ConfigureAwait(false))
            {
                logger?.Warn("[CollectionProbe] Exophase is not available.");
                return;
            }

            var steamReady = steamHunters != null && await steamHunters.BeginRunAsync(ct).ConfigureAwait(false);
            WriteLibraryCollections(api, data, logger, outputDirectory);
            try
            {
                // Each game's PlayStation link, handed to its Xbox case as the overlay hands one
                // source's link to another.
                var playStationLinks = new Dictionary<int, string>();
                foreach (var (appId, name, branch) in SteamCollections)
                {
                    await WriteReportAsync(logger, outputDirectory, $"{name} {branch}", async report =>
                    {
                        if (!steamReady)
                        {
                            report.AppendLine($"{name}: SteamHunters source unavailable");
                            return;
                        }

                        var request = new MetadataLinkRequest(
                            new Game(name) { Id = Guid.NewGuid() },
                            new GameAchievementData { GameName = name, ProviderKey = "Steam" });
                        var set = await steamHunters.FetchAsync(request, appId.ToString(), ct).ConfigureAwait(false);
                        var rows = set?.Achievements?.Where(row => row != null).ToList() ?? new List<AchievementDetail>();
                        var otherLinks = branch == ExophaseMetadataBranch.Xbox.Key && playStationLinks.TryGetValue(appId, out var playStationLink)
                            ? new Dictionary<string, string> { [ExophaseMetadataBranch.PlayStation.Key] = playStationLink }
                            : null;
                        var link = await ProbeTargetAsync(providers[branch], client, $"{name} (Steam app {appId}, via SteamHunters) -> Exophase {branch}", name, "Steam", rows, report, ct, otherLinks).ConfigureAwait(false);
                        if (branch == ExophaseMetadataBranch.PlayStation.Key && link != null)
                        {
                            playStationLinks[appId] = link;
                        }
                    }, ct).ConfigureAwait(false);
                }

                foreach (var game in selected ?? Enumerable.Empty<Game>())
                {
                    await WriteReportAsync(logger, outputDirectory, game.Name, async report =>
                    {
                        var stored = data?.GetGameAchievementData(game.Id) ?? data?.GetRawGameAchievementData(game.Id);
                        var rows = stored?.Achievements?.Where(row => row != null).ToList() ?? new List<AchievementDetail>();
                        await ProbeTargetAsync(playStation, client, $"{game.Name} (library, {stored?.ProviderKey ?? "no data"})", game.Name, stored?.ProviderKey, rows, report, ct).ConfigureAwait(false);
                    }, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                playStation.EndRun();
                if (steamReady)
                {
                    steamHunters.EndRun();
                }
            }

            logger?.Info($"[CollectionProbe] done; reports saved to {outputDirectory}");
        }

        /// <summary>
        /// library-collections.txt: the library's games that count as collections (two or more
        /// base categories), with their categories and current capstones, to try per-game capstones on.
        /// </summary>
        private static void WriteLibraryCollections(IPlayniteAPI api, AchievementDataService data, ILogger logger, string outputDirectory)
        {
            var report = new StringBuilder();
            try
            {
                foreach (var game in api?.Database?.Games?.OrderBy(game => game.Name) ?? Enumerable.Empty<Game>())
                {
                    var stored = data?.GetGameAchievementData(game.Id);
                    var rows = stored?.Achievements?.Where(row => row != null).ToList();
                    if (rows == null || rows.Count == 0)
                    {
                        continue;
                    }

                    var subGames = MetadataSubGame.From(rows);
                    if (subGames.Count < 2)
                    {
                        continue;
                    }

                    var capstones = rows.Where(row => row.IsCapstone).Select(row => $"{row.DisplayName} ({row.Category})").ToList();
                    report.AppendLine($"{game.Name} [{stored.ProviderKey}]: {string.Join("; ", subGames.Select(sub => $"{sub.Name} {sub.Rows.Count}"))}");
                    report.AppendLine($"  capstones: {(capstones.Count == 0 ? "none" : string.Join("; ", capstones))}");
                }
            }
            catch (Exception ex)
            {
                report.AppendLine($"failed: {ex.GetType().Name}: {ex.Message}");
            }

            File.WriteAllText(Path.Combine(outputDirectory, "library-collections.txt"), report.ToString(), Encoding.UTF8);
            logger?.Info($"[CollectionProbe] library collections: {report.ToString().Replace("\r", "").Replace("\n", " || ")}");
        }

        private static async Task WriteReportAsync(ILogger logger, string outputDirectory, string name, Func<StringBuilder, Task> probe, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var report = new StringBuilder();
            try
            {
                await probe(report).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                report.AppendLine($"failed: {ex.GetType().Name}: {ex.Message}");
            }

            File.WriteAllText(Path.Combine(outputDirectory, FileStem(name) + ".txt"), report.ToString(), Encoding.UTF8);
            logger?.Info($"[CollectionProbe] {report.ToString().Replace("\r", "").Replace("\n", " || ")}");
        }

        /// <summary>Probes one game; returns the link the branch found for it, or null.</summary>
        private static async Task<string> ProbeTargetAsync(
            ExophaseMetadataProvider provider,
            ExophaseApiClient client,
            string heading,
            string gameName,
            string providerKey,
            List<AchievementDetail> rows,
            StringBuilder report,
            CancellationToken ct,
            IReadOnlyDictionary<string, string> otherLinks = null)
        {
            report.AppendLine($"{heading}: {rows.Count} achievements");
            if (rows.Count == 0)
            {
                return null;
            }

            // Shows the app id is the game named.
            report.AppendLine($"sample: {string.Join("; ", rows.Take(3).Select(row => row.DisplayName))}");

            var categories = rows
                .GroupBy(row => string.IsNullOrWhiteSpace(row.Category) ? "(none)" : row.Category.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group => (Name: group.Key, Kind: group.Select(row => row.CategoryKind).FirstOrDefault(kind => !string.IsNullOrWhiteSpace(kind)), Rows: group.ToList()))
                .ToList();
            report.AppendLine("categories:");
            foreach (var category in categories)
            {
                report.AppendLine($"  {category.Name} [{category.Kind ?? "no kind"}]: {category.Rows.Count}");
            }

            // A sub-game is a category that is not DLC, a subset or an update: Base, or no kind.
            var subGames = categories
                .Where(category => category.Name != "(none)" &&
                                   !CategoryKindHelper.IsDlc(category.Kind) &&
                                   !CategoryKindHelper.IsSubset(category.Kind) &&
                                   !CategoryKindHelper.IsUpdate(category.Kind))
                .ToList();
            var isCollection = subGames.Count >= 2;
            report.AppendLine(isCollection
                ? $"collection: yes, {subGames.Count} base categories"
                : $"collection: no ({subGames.Count} base categories)");

            // With another branch's link for the game, first as the overlay would ask with it (the
            // "Also on" pages it points to judged before any search), then on its own.
            if (otherLinks?.Count > 0)
            {
                await ProbeResolutionAsync(provider, gameName, providerKey, rows, report, ct, otherLinks,
                    "resolution with the other branch's link (" + string.Join(", ", otherLinks.Select(pair => pair.Key + ": " + pair.Value)) + ")").ConfigureAwait(false);
            }

            var link = await ProbeResolutionAsync(provider, gameName, providerKey, rows, report, ct).ConfigureAwait(false);
            if (link.Matched < rows.Count * SurveyBelowShare)
            {
                await SurveyAsync(provider, client, gameName, providerKey, rows, categories.Where(category => category.Rows.Count >= 5).Select(category => category.Name), report, ct).ConfigureAwait(false);
            }

            return link.Id;
        }

        // A case whose resolution matches less than this share gets a survey of the pages offered.
        private const double SurveyBelowShare = 0.9;

        private const int SurveyPagesScored = 16;

        /// <summary>
        /// Every page on the branch's platforms that a search for the game's name or one of its
        /// categories offers, with how many of each category's achievements it matches: shows how
        /// the platform splits the game (DLC as its own lists, or lists that mix categories).
        /// </summary>
        private static async Task SurveyAsync(
            ExophaseMetadataProvider provider,
            ExophaseApiClient client,
            string gameName,
            string providerKey,
            List<AchievementDetail> rows,
            IEnumerable<string> categoryNames,
            StringBuilder report,
            CancellationToken ct)
        {
            report.AppendLine();
            report.AppendLine("== survey: pages the searches offer ==");
            var request = new MetadataLinkRequest(
                new Game(gameName) { Id = Guid.NewGuid() },
                new GameAchievementData { GameName = gameName, ProviderKey = providerKey, Achievements = rows })
            {
                WantedFields = provider.SupportedFields
            };
            var onPlatform = string.Equals(provider.Key, "Xbox", StringComparison.OrdinalIgnoreCase)
                ? (Func<string, bool>)(slug => slug.IndexOf("xbox", StringComparison.OrdinalIgnoreCase) >= 0 || slug.StartsWith("windows", StringComparison.OrdinalIgnoreCase))
                : PlayStationPlatforms.Contains;
            var pages = new List<(string Slug, string Title, string Query)>();
            var queries = new[] { gameName }.Concat(categoryNames)
                .Select(ExophaseGameNameMatcher.NormalizeGameName)
                .Where(query => !string.IsNullOrWhiteSpace(query))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var query in queries)
            {
                var results = await client.SearchGamesAsync(query, null, ct).ConfigureAwait(false) ?? new List<ExophaseGame>();
                foreach (var result in results)
                {
                    var slug = ExophaseApiClient.ExtractSlugFromUrl(result?.EndpointAwards);
                    var platforms = (result?.Platforms ?? new List<ExophasePlatform>()).Select(entry => entry?.Slug).Where(entry => entry != null);
                    if (slug != null && platforms.Any(onPlatform) && pages.All(page => !string.Equals(page.Slug, slug, StringComparison.OrdinalIgnoreCase)))
                    {
                        pages.Add((slug, result.Title, query));
                    }
                }
            }

            report.AppendLine($"  {pages.Count} pages found{(pages.Count > SurveyPagesScored ? $", first {SurveyPagesScored} scored" : string.Empty)}");
            foreach (var page in pages.Take(SurveyPagesScored))
            {
                var set = await provider.FetchAsync(request, page.Slug, ct).ConfigureAwait(false);
                var pageRows = set?.Achievements?.ToList() ?? new List<AchievementDetail>();
                var matches = CrossSourceAchievementMatcher.Match(rows, pageRows);
                var byCategory = matches
                    .GroupBy(match => string.IsNullOrWhiteSpace(match.Target.Category) ? "(none)" : match.Target.Category.Trim(), StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(group => group.Count())
                    .Select(group => $"{group.Key} {group.Count()}");
                report.AppendLine($"  {page.Slug} '{page.Title}' (from '{page.Query}'): {pageRows.Count} rows, matched {matches.Count}");
                if (matches.Count > 0)
                {
                    report.AppendLine($"    by category: {string.Join("; ", byCategory)}");
                }
            }
        }

        /// <summary>
        /// The link the branch's lookup finds for the whole game, as the overlay would ask for it,
        /// its pages fetched list by list, and how many achievements they match pooled and list by
        /// list, with the overlay's acceptance.
        /// </summary>
        private static async Task<(string Id, int Matched)> ProbeResolutionAsync(
            ExophaseMetadataProvider provider,
            string gameName,
            string providerKey,
            List<AchievementDetail> rows,
            StringBuilder report,
            CancellationToken ct,
            IReadOnlyDictionary<string, string> otherLinks = null,
            string heading = "resolution (as the overlay runs it)")
        {
            report.AppendLine();
            report.AppendLine($"== {heading} ==");
            var request = new MetadataLinkRequest(
                new Game(gameName) { Id = Guid.NewGuid() },
                new GameAchievementData { GameName = gameName, ProviderKey = providerKey, Achievements = rows })
            {
                WantedFields = provider.SupportedFields,
                OtherLinks = otherLinks ?? new Dictionary<string, string>()
            };
            var started = DateTime.UtcNow;
            var link = await provider.ResolveLinkAsync(request, ct).ConfigureAwait(false);
            if (link?.Id == null)
            {
                report.AppendLine($"  link: {(link?.IsUnavailable == true ? "unavailable, pages could not be read" : "none")} ({(DateTime.UtcNow - started).TotalSeconds:0.0} s)");
                if (link?.Trace != null)
                {
                    report.AppendLine($"  trace: {link.Trace}");
                }

                return (null, 0);
            }

            var set = await MetadataListFetch.FetchAsync(provider, request, link.Id, null, ct).ConfigureAwait(false);
            var sources = set?.Achievements?.ToList() ?? new List<AchievementDetail>();
            var pooled = CrossSourceAchievementMatcher.Match(rows, sources).Count;
            var byList = MetadataOverlayRun.MatchByList(rows, sources, set?.Lists, matchApiNames: false).Count;
            report.AppendLine($"  link: {link.Id} ({(DateTime.UtcNow - started).TotalSeconds:0.0} s)");
            report.AppendLine($"  trace: {link.Trace ?? "none"}");
            foreach (var list in set?.Lists ?? Array.Empty<MetadataSourceList>())
            {
                report.AppendLine($"    list {list.Id}: {list.Rows.Count} rows");
            }

            report.AppendLine(
                $"  matched pooled {pooled}/{rows.Count}, list by list {byList}/{rows.Count}; " +
                $"overlay accepts: {MetadataPageSearch.IsEnough(byList, rows.Count, sources.Count)}");
            return (link.Id, byList);
        }

        private static string FileStem(string name)
        {
            var stem = Regex.Replace(name ?? "game", @"[^A-Za-z0-9]+", "-").Trim('-');
            return stem.Length == 0 ? "game" : stem;
        }
    }
}
