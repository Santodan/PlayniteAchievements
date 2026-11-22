using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Providers.BattleNet.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.BattleNet
{
    /// <summary>
    /// Developer-only check of which Blizzard API surfaces serve StarCraft II, WoW Classic and WoW
    /// Forever achievement data, and which Data for Azeroth versions serve rarity. Built to run on a
    /// tester's machine: it uses the tester's saved client credentials, discovers public players from
    /// ladders and leaderboards, and reads the tester's own signed-in account for counts only. It
    /// never writes the client secret, a token, an account id, a character or realm name, or a cookie
    /// value. Writes report.txt plus raw public JSON samples, then zips the folder into one file to
    /// send back.
    /// </summary>
    internal static class BattleNetGamesProbe
    {
        /// <summary>Hardcoded gate for the probe menu item. Turn off before a release.</summary>
        internal static readonly bool Enabled = false;

        /// <summary>Limits the run to the Forever rounds and its catalog dump.</summary>
        private static readonly bool ForeverOnly = true;

        internal const string ZipName = "battlenet-probe.zip";

        private const string LogPrefix = "[BattleNet/GamesProbe]";
        private const string Locale = "en_US";
        private const string DumpLocale = "de_DE";
        private const int MaxSavedBytes = 4 * 1024 * 1024;

        /// <summary>
        /// Namespace infixes to try. Retail has none; "classic" is progression Classic, "classic1x"
        /// Era, "classicann" Anniversary. The Forever candidates are guesses, since none is documented.
        /// </summary>
        private static readonly string[][] WowVersions =
        {
            new[] { "retail", "" },
            new[] { "classic", "classic-" },
            new[] { "classic1x", "classic1x-" },
            new[] { "classicann", "classicann-" },
            new[] { "forever", "forever-" },
            new[] { "classicplus", "classicplus-" },
            new[] { "camelot", "camelot-" }
        };

        private static readonly string[] DfaVersions =
        {
            "retail", "classic", "forever", "classic-era", "classicera", "era", "mop", "cata", "anniversary"
        };

        /// <summary>Runs every round and returns the path of the zip to send back.</summary>
        public static async Task<string> RunAsync(IPlayniteAPI api, ILogger logger, string folder, CancellationToken ct)
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }

            Directory.CreateDirectory(folder);
            var report = new StringBuilder();
            report.AppendLine($"Battle.net probe, {DateTime.Now:yyyy-MM-dd HH:mm:ss}.");
            report.AppendLine();

            var settings = ProviderRegistry.Settings<BattleNetSettings>();
            var region = NormalizeRegion(settings?.WowRegion);
            report.AppendLine($"region={region}, credentials={BattleNetGameSupport.HasApiCredentials(settings)}, oauth={BattleNetGameSupport.HasOAuthSession(settings)}");
            report.AppendLine();

            CatalogDump foreverDump = null;
            try
            {
                if (!BattleNetGameSupport.HasApiCredentials(settings))
                {
                    report.AppendLine("No Battle.net client ID and secret are saved in Battle.net settings, so the Blizzard rounds were skipped.");
                }
                else
                {
                    using (var http = CreateHttpClient())
                    using (var apiClient = new BattleNetApiClient(logger, http))
                    {
                        var token = default(string);
                        try
                        {
                            token = await apiClient.GetClientCredentialsAccessTokenAsync(
                                region, settings.BattleNetClientId, settings.BattleNetClientSecret, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (!(ex is OperationCanceledException))
                        {
                            report.AppendLine($"client credentials token failed: {ex.GetType().Name}: {Redact(ex.Message, settings)}");
                        }

                        if (!string.IsNullOrWhiteSpace(token))
                        {
                            var ctx = new ProbeContext(http, folder, report, settings, token);
                            if (!ForeverOnly)
                            {
                                await RunSc2Async(ctx, region, ct).ConfigureAwait(false);
                            }

                            foreach (var version in WowVersions.Where(v => !ForeverOnly || v[0] == "forever"))
                            {
                                await RunWowVersionAsync(ctx, region, version[0], version[1], ct).ConfigureAwait(false);
                            }

                            foreverDump = ctx.ForeverDump;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                report.AppendLine($"Blizzard rounds stopped: {ex.GetType().Name}: {Redact(ex.Message, settings)}");
            }

            try
            {
                await RunDataForAzerothAsync(api, logger, folder, report, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                report.AppendLine($"Data for Azeroth round stopped: {ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                ReportForeverDuplicates(foreverDump, folder, report);
            }
            catch (Exception ex)
            {
                report.AppendLine($"Forever duplicate analysis stopped: {ex.GetType().Name}: {ex.Message}");
            }

            report.AppendLine();
            report.AppendLine("Done.");
            File.WriteAllText(Path.Combine(folder, "report.txt"), Redact(report.ToString(), settings), Encoding.UTF8);

            // The Desktop, so a tester can find the file without knowing where Playnite keeps data.
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var zipPath = Path.Combine(
                Directory.Exists(desktop) ? desktop : (Path.GetDirectoryName(folder) ?? folder),
                ZipName);
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            ZipFile.CreateFromDirectory(folder, zipPath);
            logger?.Info($"{LogPrefix} Report zipped to {zipPath}");
            return zipPath;
        }

        // --- StarCraft II ---

        private static async Task RunSc2Async(ProbeContext ctx, string region, CancellationToken ct)
        {
            var regionId = RegionToSc2Id(region);
            var host = $"https://{region}.api.blizzard.com";
            ctx.Report.AppendLine($"===== StarCraft II (region {region}, id {regionId})");

            var staticBody = await ctx.GetAsync($"{host}/sc2/static/profile/{regionId}?locale={Locale}", "sc2-static-profile.json", ct).ConfigureAwait(false);
            await ctx.GetAsync($"{host}/sc2/legacy/data/achievements/{regionId}?locale={Locale}", "sc2-legacy-achievements.json", ct).ConfigureAwait(false);

            var definitions = TryDeserialize<Sc2AchievementDefinitionsResponse>(staticBody);
            ctx.Report.AppendLine($"static catalog: achievements={definitions?.Achievements?.Count ?? 0}, categories={definitions?.Categories?.Count ?? 0}, withIcon={definitions?.Achievements?.Count(a => !string.IsNullOrWhiteSpace(a?.ImageUrl)) ?? 0}");

            var ladderBody = await ctx.GetAsync($"{host}/sc2/ladder/grandmaster/{regionId}", "sc2-grandmaster.json", ct).ConfigureAwait(false);
            var members = ReadLadderMembers(ladderBody).Take(3).ToList();
            ctx.Report.AppendLine($"grandmaster members read: {members.Count}");

            var index = 0;
            foreach (var member in members)
            {
                index++;
                var path = $"{member.Region}/{member.Realm}/{member.Id}";
                ctx.Report.AppendLine($"-- profile {index}: {member.DisplayName} ({path})");
                var profileBody = await ctx.GetAsync($"{host}/sc2/profile/{path}?locale={Locale}", $"sc2-profile-{index}.json", ct).ConfigureAwait(false);
                await ctx.GetAsync($"{host}/sc2/legacy/profile/{path}?locale={Locale}", $"sc2-legacy-profile-{index}.json", ct).ConfigureAwait(false);

                ReportSc2Mapping(ctx, definitions, TryDeserialize<Sc2ProfileResponse>(profileBody), sampleRows: true);
            }

            if (BattleNetGameSupport.HasOAuthSession(ctx.Settings))
            {
                ctx.Report.AppendLine("-- account profiles (tester's own sign-in; counts only)");
                var body = await ctx.GetAsync(
                    $"{host}/sc2/player/{Uri.EscapeDataString(ctx.Settings.BattleNetAccountId)}",
                    saveAs: null,
                    ct,
                    bearer: ctx.Settings.BattleNetAccessToken).ConfigureAwait(false);
                var profiles = TryDeserialize<List<Sc2PlayerProfile>>(body);
                ctx.Report.AppendLine($"account profiles={profiles?.Count ?? 0}, regions={string.Join(",", (profiles ?? new List<Sc2PlayerProfile>()).Select(p => p?.RegionId ?? 0).Distinct())}");

                var chosen = Sc2GameStrategy.ChooseProfile(profiles, region);
                if (chosen != null)
                {
                    var profileHost = $"https://{BattleNetApiClient.MapSc2RegionIdToApiRegion(chosen.RegionId)}.api.blizzard.com";
                    var profileBody = await ctx.GetAsync(
                        $"{profileHost}/sc2/profile/{chosen.RegionId}/{chosen.RealmId}/{chosen.ProfileId}?locale={Locale}",
                        saveAs: null,
                        ct,
                        shownAs: $"/sc2/profile/<account profile, region id {chosen.RegionId}>").ConfigureAwait(false);
                    ReportSc2Mapping(ctx, definitions, TryDeserialize<Sc2ProfileResponse>(profileBody), sampleRows: false);
                }
            }

            ctx.Report.AppendLine();
        }

        /// <summary>
        /// Runs a parsed profile through the provider's mapping and reports the counts. Sample rows
        /// are for public ladder players only; the tester's own profile reports counts.
        /// </summary>
        private static void ReportSc2Mapping(ProbeContext ctx, Sc2AchievementDefinitionsResponse definitions, Sc2ProfileResponse profile, bool sampleRows)
        {
            if (profile == null || definitions == null)
            {
                ctx.Report.AppendLine("mapping skipped: catalog or profile did not parse");
                return;
            }

            var rows = Sc2GameStrategy.BuildAchievements(definitions, profile);
            var unlocked = rows.Where(r => r.Unlocked).ToList();
            ctx.Report.AppendLine(
                $"mapped: rows={rows.Count}, unlocked={unlocked.Count}, unlockedWithTime={unlocked.Count(r => r.UnlockTimeUtc.HasValue)}, " +
                $"withCategory={rows.Count(r => !string.IsNullOrWhiteSpace(r.Category))}, " +
                $"earnedListed={profile.EarnedAchievements?.Count ?? 0}, profilePoints={profile.Summary?.TotalAchievementPoints ?? 0}, " +
                $"mappedPoints={unlocked.Sum(r => r.Points ?? 0)}");
            if (!sampleRows)
            {
                return;
            }

            foreach (var row in unlocked.OrderByDescending(r => r.UnlockTimeUtc).Take(5))
            {
                ctx.Report.AppendLine($"  {row.ApiName} | {row.DisplayName} | {row.Category} | {row.UnlockTimeUtc:yyyy-MM-dd}");
            }
        }

        private static IEnumerable<LadderMember> ReadLadderMembers(string body)
        {
            var root = TryParse(body);
            var teams = root?["ladderTeams"] as JArray;
            foreach (var team in teams ?? new JArray())
            {
                foreach (var member in (team?["teamMembers"] as JArray) ?? new JArray())
                {
                    var id = member?.Value<string>("id");
                    var realm = member?.Value<int?>("realm") ?? 0;
                    var regionId = member?.Value<int?>("region") ?? 0;
                    if (!string.IsNullOrWhiteSpace(id) && realm > 0 && regionId > 0)
                    {
                        yield return new LadderMember
                        {
                            Id = id,
                            Realm = realm,
                            Region = regionId,
                            DisplayName = member.Value<string>("displayName")
                        };
                    }
                }
            }
        }

        // --- World of Warcraft versions ---

        private static async Task RunWowVersionAsync(ProbeContext ctx, string region, string label, string infix, CancellationToken ct)
        {
            var host = $"https://{region}.api.blizzard.com";
            var staticNs = $"static-{infix}{region}";
            var dynamicNs = $"dynamic-{infix}{region}";
            var profileNs = $"profile-{infix}{region}";
            ctx.Report.AppendLine($"===== WoW {label} ({staticNs} / {dynamicNs} / {profileNs})");

            var achievementIndex = await ctx.GetAsync($"{host}/data/wow/achievement/index?namespace={staticNs}&locale={Locale}", $"wow-{label}-achievement-index.json", ct).ConfigureAwait(false);
            ctx.Report.AppendLine($"achievement index entries={CountArray(achievementIndex, "achievements")}");
            var categoryIndex = await ctx.GetAsync($"{host}/data/wow/achievement-category/index?namespace={staticNs}&locale={Locale}", $"wow-{label}-category-index.json", ct).ConfigureAwait(false);
            ctx.Report.AppendLine($"category index entries={CountArray(categoryIndex, "categories")}, root={CountArray(categoryIndex, "root_categories")}");

            var firstAchievementId = (TryParse(achievementIndex)?["achievements"] as JArray)?.FirstOrDefault()?.Value<int?>("id");
            if (firstAchievementId.HasValue)
            {
                await ctx.GetAsync($"{host}/data/wow/achievement/{firstAchievementId.Value}?namespace={staticNs}&locale={Locale}", $"wow-{label}-achievement-sample.json", ct).ConfigureAwait(false);
                await ctx.GetAsync($"{host}/data/wow/media/achievement/{firstAchievementId.Value}?namespace={staticNs}", $"wow-{label}-achievement-media-sample.json", ct).ConfigureAwait(false);
            }

            if (label == "forever")
            {
                ctx.ForeverDump = await DumpWowCatalogAsync(ctx, host, label, staticNs, achievementIndex, categoryIndex, ct).ConfigureAwait(false);
            }

            var realms = await ctx.GetAsync($"{host}/data/wow/realm/index?namespace={dynamicNs}&locale={Locale}", $"wow-{label}-realm-index.json", ct).ConfigureAwait(false);
            ctx.Report.AppendLine($"realm index entries={CountArray(realms, "realms")}");

            var characters = await FindLeaderboardCharactersAsync(ctx, host, label, dynamicNs, ct).ConfigureAwait(false);
            ctx.Report.AppendLine($"leaderboard characters found={characters.Count}");

            var index = 0;
            foreach (var character in characters.Take(2))
            {
                index++;
                var realm = Uri.EscapeDataString(character.Item1.ToLowerInvariant());
                var name = Uri.EscapeDataString(character.Item2.ToLowerInvariant());
                ctx.Report.AppendLine($"-- character {index}: {character.Item2}-{character.Item1}");
                await ctx.GetAsync($"{host}/profile/wow/character/{realm}/{name}?namespace={profileNs}&locale={Locale}", $"wow-{label}-character-{index}.json", ct).ConfigureAwait(false);
                var achievements = await ctx.GetAsync($"{host}/profile/wow/character/{realm}/{name}/achievements?namespace={profileNs}&locale={Locale}", $"wow-{label}-character-{index}-achievements.json", ct).ConfigureAwait(false);
                var root = TryParse(achievements);
                var list = root?["achievements"] as JArray;
                ctx.Report.AppendLine(
                    $"achievements: total_quantity={root?.Value<int?>("total_quantity")}, total_points={root?.Value<int?>("total_points")}, " +
                    $"listed={list?.Count ?? 0}, completed={list?.Count(a => a?["completed_timestamp"] != null) ?? 0}");
            }

            if (BattleNetGameSupport.HasOAuthSession(ctx.Settings))
            {
                ctx.Report.AppendLine("-- account characters (tester's own sign-in; counts only)");
                var body = await ctx.GetAsync(
                    $"{host}/profile/user/wow?namespace={profileNs}&locale={Locale}",
                    saveAs: null,
                    ct,
                    bearer: ctx.Settings.BattleNetAccessToken).ConfigureAwait(false);
                var accounts = TryParse(body)?["wow_accounts"] as JArray;
                ctx.Report.AppendLine($"wow_accounts={accounts?.Count ?? 0}, characters={accounts?.Sum(a => (a?["characters"] as JArray)?.Count ?? 0) ?? 0}");

                var catalogIds = new HashSet<int>(((TryParse(achievementIndex)?["achievements"] as JArray) ?? new JArray())
                    .Select(a => a?.Value<int?>("id") ?? 0));
                var allCharacters = (accounts ?? new JArray())
                    .SelectMany(a => (a?["characters"] as JArray) ?? new JArray())
                    .OfType<JObject>()
                    .ToList();

                // Field names and presence counts only, never values, so a version whose character
                // entries differ from retail shows which fields it has.
                var first = allCharacters.FirstOrDefault();
                if (first != null)
                {
                    ctx.Report.AppendLine($"character fields: {string.Join(",", first.Properties().Select(p => p.Name))}");
                    ctx.Report.AppendLine($"character realm fields: {string.Join(",", ((first["realm"] as JObject)?.Properties() ?? Enumerable.Empty<JProperty>()).Select(p => p.Name))}");
                    ctx.Report.AppendLine(
                        $"characters with name={allCharacters.Count(c => StringField(c, "name") != null)}, " +
                        $"realm.slug={allCharacters.Count(c => StringField(c["realm"] as JObject, "slug") != null)}, " +
                        $"character.key.href={allCharacters.Count(c => CharacterHref(c) != null)}");
                }

                // Highest-level characters first: they are the ones most likely to have earned something.
                var accountIndex = 0;
                foreach (var character in allCharacters.OrderByDescending(c => c.Value<int?>("level") ?? 0).Take(2))
                {
                    accountIndex++;
                    var slug = StringField(character["realm"] as JObject, "slug");
                    var name = StringField(character, "name");
                    var href = CharacterHref(character);
                    if (href != null && (slug == null || name == null))
                    {
                        // The link's shape with the character name masked, so a version without
                        // realms shows what stands in the realm position.
                        var shape = href;
                        if (name != null)
                        {
                            shape = shape.Replace(Uri.EscapeDataString(name.ToLowerInvariant()), "<name>")
                                .Replace(name.ToLowerInvariant(), "<name>")
                                .Replace(name, "<name>");
                        }

                        ctx.Report.AppendLine($"account character {accountIndex} link: {Redact(shape, ctx.Settings)}");
                    }

                    string url;
                    if (slug != null && name != null)
                    {
                        url = $"{host}/profile/wow/character/{Uri.EscapeDataString(slug.ToLowerInvariant())}/{Uri.EscapeDataString(name.ToLowerInvariant())}/achievements?namespace={profileNs}&locale={Locale}";
                    }
                    else if (href != null)
                    {
                        url = href.Split('?')[0].TrimEnd('/') + $"/achievements?namespace={profileNs}&locale={Locale}";
                    }
                    else
                    {
                        ctx.Report.AppendLine($"account character {accountIndex}: no realm slug, name or profile link");
                        continue;
                    }

                    var achievements = await ctx.GetAsync(
                        url,
                        saveAs: null,
                        ct,
                        shownAs: $"/profile/wow/character/<account character {accountIndex}, level {character.Value<int?>("level")}, via {(slug != null && name != null ? "slug" : "href")}>/achievements").ConfigureAwait(false);
                    var root = TryParse(achievements);
                    var completed = ((root?["achievements"] as JArray) ?? new JArray())
                        .Where(a => a?["completed_timestamp"] != null)
                        .Select(a => a.Value<int?>("id") ?? 0)
                        .ToList();
                    ctx.Report.AppendLine(
                        $"achievements: total_quantity={root?.Value<int?>("total_quantity")}, total_points={root?.Value<int?>("total_points")}, " +
                        $"listed={(root?["achievements"] as JArray)?.Count ?? 0}, completed={completed.Count}, completedInCatalog={completed.Count(catalogIds.Contains)}");
                }
            }

            ctx.Report.AppendLine();
        }

        /// <summary>
        /// Finds public characters from the current PvP leaderboard. Retail exposes seasons directly;
        /// Classic nests them under a PvP region, so both shapes are tried.
        /// </summary>
        private static async Task<List<Tuple<string, string>>> FindLeaderboardCharactersAsync(
            ProbeContext ctx, string host, string label, string dynamicNs, CancellationToken ct)
        {
            var prefixes = new List<string> { "/data/wow" };
            var pvpRegions = await ctx.GetAsync($"{host}/data/wow/pvp-region/index?namespace={dynamicNs}&locale={Locale}", $"wow-{label}-pvp-regions.json", ct).ConfigureAwait(false);
            foreach (var pvpRegion in ((TryParse(pvpRegions)?["pvp_regions"] as JArray) ?? new JArray()).Take(2))
            {
                var id = pvpRegion?.Value<int?>("id") ?? ExtractTrailingId(pvpRegion?["key"]?.Value<string>("href"));
                if (id.HasValue)
                {
                    prefixes.Add($"/data/wow/pvp-region/{id.Value}");
                }
            }

            foreach (var prefix in prefixes)
            {
                var seasons = await ctx.GetAsync($"{host}{prefix}/pvp-season/index?namespace={dynamicNs}&locale={Locale}", saveAs: null, ct).ConfigureAwait(false);
                var seasonRoot = TryParse(seasons);
                var seasonId = seasonRoot?["current_season"]?.Value<int?>("id")
                    ?? (seasonRoot?["seasons"] as JArray)?.Select(s => s?.Value<int?>("id")).Where(s => s.HasValue).Select(s => s.Value).DefaultIfEmpty(0).Max();
                if (!seasonId.HasValue || seasonId.Value <= 0)
                {
                    continue;
                }

                var boards = await ctx.GetAsync($"{host}{prefix}/pvp-season/{seasonId.Value}/pvp-leaderboard/index?namespace={dynamicNs}&locale={Locale}", saveAs: null, ct).ConfigureAwait(false);
                var boardNames = ((TryParse(boards)?["leaderboards"] as JArray) ?? new JArray())
                    .Select(b => b?.Value<string>("name") ?? ExtractLastSegment(b?["key"]?.Value<string>("href")))
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .OrderBy(n => n.IndexOf("3v3", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1)
                    .Take(2)
                    .ToList();

                foreach (var board in boardNames)
                {
                    var body = await ctx.GetAsync($"{host}{prefix}/pvp-season/{seasonId.Value}/pvp-leaderboard/{Uri.EscapeDataString(board)}?namespace={dynamicNs}&locale={Locale}", saveAs: null, ct).ConfigureAwait(false);
                    var found = ((TryParse(body)?["entries"] as JArray) ?? new JArray())
                        .Select(e => e?["character"])
                        .Where(c => c != null)
                        .Select(c => Tuple.Create(c["realm"]?.Value<string>("slug"), c.Value<string>("name")))
                        .Where(t => !string.IsNullOrWhiteSpace(t.Item1) && !string.IsNullOrWhiteSpace(t.Item2))
                        .Take(5)
                        .ToList();
                    if (found.Count > 0)
                    {
                        ctx.Report.AppendLine($"characters from {prefix} season {seasonId.Value} board {board}");
                        return found;
                    }
                }
            }

            return new List<Tuple<string, string>>();
        }

        // --- Catalog dump and duplicate analysis ---

        /// <summary>Every category and achievement resource of one version's static catalog.</summary>
        private sealed class CatalogDump
        {
            public Dictionary<int, JObject> Achievements { get; } = new Dictionary<int, JObject>();
            public Dictionary<int, JObject> Categories { get; } = new Dictionary<int, JObject>();
            public HashSet<int> IndexIds { get; } = new HashSet<int>();
        }

        /// <summary>
        /// Saves every category and achievement resource the version's indexes list, in the locale a
        /// German report used, so the duplicate analysis can compare full resources.
        /// </summary>
        private static async Task<CatalogDump> DumpWowCatalogAsync(
            ProbeContext ctx, string host, string label, string staticNs, string achievementIndex, string categoryIndex, CancellationToken ct)
        {
            var dump = new CatalogDump();
            ctx.Report.AppendLine($"-- {label} catalog dump ({DumpLocale})");

            var categoryRoot = TryParse(categoryIndex);
            var categoryIds = new[] { "categories", "root_categories", "guild_categories" }
                .SelectMany(key => (categoryRoot?[key] as JArray) ?? new JArray())
                .Select(c => c?.Value<int?>("id") ?? 0)
                .Where(id => id > 0)
                .Distinct()
                .OrderBy(id => id)
                .ToList();
            foreach (var id in categoryIds)
            {
                ct.ThrowIfCancellationRequested();
                var root = TryParse(await ctx.GetAsync(
                    $"{host}/data/wow/achievement-category/{id}?namespace={staticNs}&locale={DumpLocale}",
                    $"wow-{label}-category-{id}.json",
                    ct).ConfigureAwait(false));
                if (root != null)
                {
                    dump.Categories[id] = root;
                }
            }

            foreach (var id in ((TryParse(achievementIndex)?["achievements"] as JArray) ?? new JArray())
                .Select(a => a?.Value<int?>("id") ?? 0)
                .Where(id => id > 0)
                .Distinct()
                .OrderBy(id => id))
            {
                ct.ThrowIfCancellationRequested();
                dump.IndexIds.Add(id);
                var root = TryParse(await ctx.GetAsync(
                    $"{host}/data/wow/achievement/{id}?namespace={staticNs}&locale={DumpLocale}",
                    $"wow-{label}-achievement-{id}.json",
                    ct).ConfigureAwait(false));
                if (root != null)
                {
                    dump.Achievements[id] = root;
                }
            }

            ctx.Report.AppendLine($"dumped categories={dump.Categories.Count}/{categoryIds.Count}, achievements={dump.Achievements.Count}/{dump.IndexIds.Count}");
            return dump;
        }

        /// <summary>
        /// Compares achievements that share a name, and splits every catalog field by whether Data for
        /// Azeroth rates the achievement, to find a field that separates the rated set from the rest.
        /// </summary>
        private static void ReportForeverDuplicates(CatalogDump dump, string folder, StringBuilder report)
        {
            report.AppendLine("===== Forever duplicate analysis");
            if (dump == null || dump.Achievements.Count == 0)
            {
                report.AppendLine("skipped: no Forever achievement resources were fetched");
                report.AppendLine();
                return;
            }

            HashSet<int> rated = null;
            var rarityFile = Path.Combine(folder, "dfa-forever-achievementsrarity.json");
            if (File.Exists(rarityFile))
            {
                var map = TryParse(File.ReadAllText(rarityFile))?["achievements"] as JObject;
                if (map != null)
                {
                    rated = new HashSet<int>(map.Properties()
                        .Select(p => int.TryParse(p.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0)
                        .Where(id => id > 0));
                }
            }

            string RatedLabel(int id) => rated == null ? "rarity?" : rated.Contains(id) ? "rated" : "UNRATED";

            var listedBy = new Dictionary<int, List<int>>();
            foreach (var category in dump.Categories)
            {
                foreach (var id in ((category.Value["achievements"] as JArray) ?? new JArray()).Select(a => a?.Value<int?>("id") ?? 0).Where(id => id > 0))
                {
                    if (!listedBy.TryGetValue(id, out var list))
                    {
                        listedBy[id] = list = new List<int>();
                    }

                    list.Add(category.Key);
                }
            }

            report.AppendLine(
                $"achievements={dump.Achievements.Count}, indexIds={dump.IndexIds.Count}, " +
                $"indexIdsNoCategoryLists={dump.IndexIds.Count(id => !listedBy.ContainsKey(id))}, " +
                $"categoryListedIdsNotInIndex={listedBy.Keys.Count(id => !dump.IndexIds.Contains(id))}, " +
                $"listedByMoreThanOneCategory={listedBy.Count(p => p.Value.Count > 1)}");
            report.AppendLine(rated == null
                ? "rarity: no dfa-forever-achievementsrarity.json, so rated/unrated splits are unavailable"
                : $"rarity: rated={dump.IndexIds.Count(rated.Contains)} of {dump.IndexIds.Count}, ratedIdsNotInIndex={rated.Count(id => !dump.IndexIds.Contains(id))}");

            report.AppendLine("-- categories (id | name | parent | listed | rated)");
            foreach (var category in dump.Categories.OrderBy(c => c.Key))
            {
                var ids = ((category.Value["achievements"] as JArray) ?? new JArray()).Select(a => a?.Value<int?>("id") ?? 0).ToList();
                report.AppendLine(
                    $"  {category.Key} | {category.Value.Value<string>("name")} | " +
                    $"{category.Value["parent_category"]?.Value<int?>("id")} {category.Value["parent_category"]?.Value<string>("name")} | " +
                    $"{ids.Count} | {(rated == null ? "?" : ids.Count(rated.Contains).ToString(CultureInfo.InvariantCulture))}");
            }

            // Achievements sharing a name, with every leaf value that differs inside the group.
            var groups = dump.Achievements
                .GroupBy(a => (a.Value.Value<string>("name") ?? string.Empty).Trim().ToLowerInvariant())
                .Where(g => g.Key.Length > 0 && g.Count() > 1)
                .OrderBy(g => g.Min(a => a.Key))
                .ToList();
            report.AppendLine($"-- same-name groups={groups.Count}, achievements in them={groups.Sum(g => g.Count())}");
            foreach (var group in groups)
            {
                report.AppendLine($"[{group.First().Value.Value<string>("name")}]");
                foreach (var member in group.OrderBy(a => a.Key))
                {
                    var a = member.Value;
                    report.AppendLine(
                        $"  {member.Key} {RatedLabel(member.Key)} category={a["category"]?.Value<int?>("id")} {a["category"]?.Value<string>("name")} " +
                        $"listedBy={(listedBy.TryGetValue(member.Key, out var by) ? string.Join("/", by) : "none")} " +
                        $"display_order={a.Value<int?>("display_order")} desc=\"{a.Value<string>("description")}\"");
                }

                var flattened = group.ToDictionary(a => a.Key, a => Flatten(a.Value));
                var paths = flattened.Values.SelectMany(f => f.Keys).Distinct().OrderBy(p => p, StringComparer.Ordinal);
                foreach (var path in paths)
                {
                    var values = group.OrderBy(a => a.Key)
                        .Select(a => flattened[a.Key].TryGetValue(path, out var v) ? v : "<absent>")
                        .ToList();
                    if (values.Distinct().Count() > 1 && path != "id" && path != "description")
                    {
                        report.AppendLine($"    {path}: {string.Join(" | ", values)}");
                    }
                }
            }

            // Each structural feature, counted over rated and unrated achievements across the catalog.
            if (rated != null)
            {
                var split = new Dictionary<string, int[]>(StringComparer.Ordinal);
                foreach (var achievement in dump.Achievements)
                {
                    var slot = rated.Contains(achievement.Key) ? 0 : 1;
                    foreach (var feature in Features(achievement.Value, listedBy.ContainsKey(achievement.Key)))
                    {
                        if (!split.TryGetValue(feature, out var counts))
                        {
                            split[feature] = counts = new int[2];
                        }

                        counts[slot]++;
                    }
                }

                var ratedTotal = dump.Achievements.Keys.Count(rated.Contains);
                report.AppendLine($"-- feature split over the catalog (rated total={ratedTotal}, unrated total={dump.Achievements.Count - ratedTotal}); rated | unrated | feature");
                foreach (var entry in split.OrderByDescending(e => Math.Abs(e.Value[0] - e.Value[1])).ThenBy(e => e.Key, StringComparer.Ordinal))
                {
                    report.AppendLine($"  {entry.Value[0]} | {entry.Value[1]} | {entry.Key}");
                }
            }

            report.AppendLine();
        }

        /// <summary>Leaf values of a resource by JSON path, without links.</summary>
        private static Dictionary<string, string> Flatten(JObject root)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var leaf in root.Descendants().OfType<JValue>())
            {
                var path = leaf.Path;
                if (path.StartsWith("_links", StringComparison.Ordinal) || path.EndsWith(".href", StringComparison.Ordinal))
                {
                    continue;
                }

                result[path] = leaf.Type == JTokenType.Null ? "null" : leaf.ToString(Formatting.None);
            }

            return result;
        }

        /// <summary>Presence and small-value features of an achievement resource, without ids or text.</summary>
        private static IEnumerable<string> Features(JObject a, bool listedByCategory)
        {
            yield return $"listed by a category={listedByCategory}";
            foreach (var property in a.Properties())
            {
                yield return $"has {property.Name}";
            }

            var criteria = a["criteria"] as JObject;
            foreach (var property in criteria?.Properties() ?? Enumerable.Empty<JProperty>())
            {
                yield return $"has criteria.{property.Name}";
            }

            yield return $"is_account_wide={a.Value<bool?>("is_account_wide")}";
            yield return $"points={a.Value<int?>("points")}";
            yield return $"criteria.amount={criteria?.Value<long?>("amount")}";
            yield return $"criteria.operator.type={criteria?["operator"]?.Value<string>("type")}";
            yield return $"child criteria={(criteria?["child_criteria"] as JArray)?.Count ?? 0}";
            yield return $"reward_description present={!string.IsNullOrWhiteSpace(a.Value<string>("reward_description"))}";
            yield return $"category.id={a["category"]?.Value<int?>("id")}";
        }

        // --- Data for Azeroth ---

        private static async Task RunDataForAzerothAsync(IPlayniteAPI api, ILogger logger, string folder, StringBuilder report, CancellationToken ct)
        {
            report.AppendLine("===== Data for Azeroth");
            var header = string.Empty;
            if (api != null)
            {
                var views = new OffscreenViewLeaseSource(api, logger);
                var cookies = CefCookieReader.Filter(
                    await CefCookieReader.ReadAsync(api, views, DataForAzerothClient.SiteDomain, logger, LogPrefix, ct).ConfigureAwait(false),
                    DataForAzerothClient.SiteDomain);
                header = CefCookieReader.BuildCookieHeader(cookies);
                report.AppendLine($"cookies: count={cookies.Count}, names={CefCookieReader.DescribeNames(cookies)}");
            }

            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseCookies = false,
                AllowAutoRedirect = false
            };

            using (var http = new HttpClient(handler))
            {
                http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

                foreach (var version in DfaVersions.Where(v => !ForeverOnly || v == "forever"))
                {
                    var url = $"{DataForAzerothClient.BaseUrl}{version}/dynamic/index.json";
                    report.AppendLine($"-- {url}");
                    var body = await GetDfaAsync(http, url, header, report, ct).ConfigureAwait(false);
                    var root = TryParse(body);
                    if (root == null)
                    {
                        continue;
                    }

                    File.WriteAllText(Path.Combine(folder, $"dfa-{version}-index.json"), body, Encoding.UTF8);
                    report.AppendLine($"index keys: {string.Join(",", root.Properties().Select(p => p.Name).Take(40))}");
                    var rarityPath = root.Value<string>("achievementsrarity");
                    if (!string.IsNullOrWhiteSpace(rarityPath))
                    {
                        var rarity = TryParse(await GetDfaAsync(http, DataForAzerothClient.BuildDynamicUrl(rarityPath), header, report, ct).ConfigureAwait(false));
                        var map = rarity?["achievements"] as JObject;
                        report.AppendLine($"rarity entries={map?.Count ?? 0}, sample={string.Join(",", map?.Properties().Take(3).Select(p => p.Name + "=" + p.Value) ?? Enumerable.Empty<string>())}");
                    }

                    // Forever: save every file the index lists, to find where the site's Forever
                    // percentages come from. Retail: the achievement files only, for comparison.
                    foreach (var entry in root.Properties())
                    {
                        var path = entry.Value.Type == JTokenType.String ? entry.Value.Value<string>() : null;
                        var wanted = version == "forever" ||
                            (version == "retail" && (entry.Name == "achievementsrarity" || entry.Name == "achievementsseen" || entry.Name == "max"));
                        if (!wanted || string.IsNullOrWhiteSpace(path))
                        {
                            continue;
                        }

                        report.AppendLine($"-- {version}/{entry.Name} {path}");
                        var data = await GetDfaAsync(http, DataForAzerothClient.BuildDynamicUrl(path), header, report, ct).ConfigureAwait(false);
                        if (string.IsNullOrEmpty(data))
                        {
                            continue;
                        }

                        var text = data.Length > MaxSavedBytes ? data.Substring(0, MaxSavedBytes) : data;
                        File.WriteAllText(Path.Combine(folder, $"dfa-{version}-{SafeFileSegment(entry.Name)}.json"), text, Encoding.UTF8);
                        report.AppendLine(DescribeDfaShape(data));
                    }
                }

                // The site's bundle names its own data paths; list every version prefix it uses.
                report.AppendLine($"-- bundle scan {DataForAzerothClient.BaseUrl}");
                var shell = await GetDfaAsync(http, DataForAzerothClient.BaseUrl, header, report, ct).ConfigureAwait(false) ?? string.Empty;
                var scripts = Regex.Matches(shell, "src=\"([^\"]+\\.js)\"").Cast<Match>().Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();
                var prefixes = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var src in scripts)
                {
                    var js = await GetDfaAsync(http, new Uri(new Uri(DataForAzerothClient.BaseUrl), src).ToString(), header, report, ct).ConfigureAwait(false) ?? string.Empty;
                    var scriptFile = "dfa-bundle-" + SafeFileSegment(Path.GetFileName(new Uri(new Uri(DataForAzerothClient.BaseUrl), src).AbsolutePath));
                    File.WriteAllText(Path.Combine(folder, scriptFile), js.Length > MaxSavedBytes ? js.Substring(0, MaxSavedBytes) : js, Encoding.UTF8);
                    report.AppendLine($"saved {scriptFile}");

                    // Every data path the bundle names, then the code around rarity, seen and Forever
                    // hits, to show how a page turns a file into a percentage.
                    var dataPaths = Regex.Matches(js, "[\"'`]([^\"'`\\s]{0,120}(?:\\.json|/api/|/dynamic/|/static/)[^\"'`\\s]{0,120})[\"'`]")
                        .Cast<Match>().Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).Take(120).ToList();
                    report.AppendLine($"data paths ({dataPaths.Count}): {string.Join(" | ", dataPaths)}");
                    foreach (var term in new[] { "rarity", "achievementsseen", "forever", "percent" })
                    {
                        var hits = Regex.Matches(js, Regex.Escape(term), RegexOptions.IgnoreCase).Cast<Match>().ToList();
                        report.AppendLine($"term '{term}': {hits.Count} hits");
                        var lastEnd = -1;
                        foreach (var hit in hits.Take(25))
                        {
                            var start = Math.Max(0, hit.Index - 150);
                            if (start < lastEnd)
                            {
                                continue;
                            }

                            var end = Math.Min(js.Length, hit.Index + 150);
                            lastEnd = end;
                            report.AppendLine($"  @{hit.Index}: {js.Substring(start, end - start).Replace("\r", " ").Replace("\n", " ")}");
                        }
                    }

                    foreach (Match match in Regex.Matches(js, "[\"'`/]([A-Za-z0-9_\\-]{2,24})/dynamic/"))
                    {
                        prefixes.Add(match.Groups[1].Value);
                    }

                    foreach (Match match in Regex.Matches(js, "(?i)(forever|classic[A-Za-z0-9_\\-]{0,16}|era|anniversary|mop)[\"'`]"))
                    {
                        prefixes.Add("token:" + match.Groups[1].Value);
                    }
                }

                report.AppendLine($"scripts={scripts.Count}, dynamic prefixes and version tokens: {string.Join(", ", prefixes)}");
            }

            report.AppendLine();
        }

        private static async Task<string> GetDfaAsync(HttpClient http, string url, string cookieHeader, StringBuilder report, CancellationToken ct)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    if (!string.IsNullOrEmpty(cookieHeader))
                    {
                        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
                    }

                    using (var response = await http.SendAsync(request, ct).ConfigureAwait(false))
                    {
                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var type = response.Content?.Headers?.ContentType?.MediaType ?? "<none>";
                        report.AppendLine(
                            $"status={(int)response.StatusCode}, type={type}, length={body?.Length ?? 0}, " +
                            $"botCheck={DataForAzerothClient.LooksLikeBotCheck(body)}, location={response.Headers.Location?.ToString() ?? "<none>"}");
                        return body;
                    }
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                report.AppendLine($"failed: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        // --- Helpers ---

        private sealed class ProbeContext
        {
            private readonly HttpClient _http;
            private readonly string _folder;
            private readonly string _token;

            public ProbeContext(HttpClient http, string folder, StringBuilder report, BattleNetSettings settings, string token)
            {
                _http = http;
                _folder = folder;
                Report = report;
                Settings = settings;
                _token = token;
            }

            public StringBuilder Report { get; }

            public BattleNetSettings Settings { get; }

            public CatalogDump ForeverDump { get; set; }

            /// <summary>
            /// GETs a Blizzard URL with the client token (or the given user token), logs a one-line
            /// summary, and saves the body when a file name is given. Account calls pass no file name,
            /// so their bodies are never written. Account calls also pass a shownAs label, so their
            /// URLs, which carry realm, character and profile ids, never reach the report.
            /// </summary>
            public async Task<string> GetAsync(string url, string saveAs, CancellationToken ct, string bearer = null, string shownAs = null)
            {
                var shownUrl = shownAs ?? Redact(url, Settings);
                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer ?? _token);
                        request.Headers.TryAddWithoutValidation("Accept", "application/json");
                        using (var response = await _http.SendAsync(request, ct).ConfigureAwait(false))
                        {
                            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false) ?? string.Empty;
                            var root = TryParse(body);
                            Report.AppendLine(
                                $"{(int)response.StatusCode} {shownUrl} type={response.Content?.Headers?.ContentType?.MediaType ?? "<none>"} " +
                                $"length={body.Length} keys={(root == null ? (body.TrimStart().StartsWith("[") ? "<array>" : "<not json>") : string.Join(",", root.Properties().Select(p => p.Name).Take(12)))}");

                            if (!response.IsSuccessStatusCode && root != null)
                            {
                                var detail = root.Value<string>("detail") ?? root.Value<string>("error_description") ?? root.Value<string>("reason");
                                if (!string.IsNullOrWhiteSpace(detail))
                                {
                                    Report.AppendLine("  detail: " + detail);
                                }
                            }

                            if (!string.IsNullOrEmpty(saveAs) && response.IsSuccessStatusCode && body.Length > 0)
                            {
                                var text = body.Length > MaxSavedBytes ? body.Substring(0, MaxSavedBytes) : body;
                                File.WriteAllText(Path.Combine(_folder, saveAs), text, Encoding.UTF8);
                            }

                            return response.IsSuccessStatusCode ? body : null;
                        }
                    }
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    Report.AppendLine($"ERR {shownUrl} {ex.GetType().Name}: {Redact(ex.Message, Settings)}");
                    return null;
                }
            }
        }

        private sealed class LadderMember
        {
            public string Id { get; set; }
            public int Realm { get; set; }
            public int Region { get; set; }
            public string DisplayName { get; set; }
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient(new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            });
            client.Timeout = TimeSpan.FromSeconds(60);
            return client;
        }

        private static string NormalizeRegion(string region)
        {
            var value = (region ?? string.Empty).Trim().ToLowerInvariant();
            return value == "eu" || value == "kr" || value == "tw" ? value : "us";
        }

        private static int RegionToSc2Id(string region)
        {
            switch (region)
            {
                case "eu": return 2;
                case "kr":
                case "tw": return 3;
                default: return 1;
            }
        }

        private static JObject TryParse(string body)
        {
            if (string.IsNullOrWhiteSpace(body) || body.TrimStart()[0] != '{')
            {
                return null;
            }

            try
            {
                return JObject.Parse(body);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string SafeFileSegment(string value) =>
            Regex.Replace(value ?? string.Empty, @"[^A-Za-z0-9_.\-]", "_");

        /// <summary>
        /// One line on a Data for Azeroth file: the top-level type and size, and for an object the
        /// first keys with a short preview of each value.
        /// </summary>
        private static string DescribeDfaShape(string body)
        {
            JToken root;
            try
            {
                root = JToken.Parse(body);
            }
            catch (JsonException ex)
            {
                return $"shape: not JSON ({ex.Message})";
            }

            string Preview(JToken token)
            {
                var text = token.ToString(Formatting.None);
                return text.Length > 160 ? text.Substring(0, 160) + "..." : text;
            }

            switch (root)
            {
                case JObject obj:
                    var lines = new StringBuilder($"shape: object, keys={obj.Count}");
                    foreach (var prop in obj.Properties().Take(4))
                    {
                        var size = prop.Value is JContainer container ? $" ({prop.Value.Type}, count={container.Count})" : string.Empty;
                        lines.Append($"{Environment.NewLine}  {prop.Name}{size}: {Preview(prop.Value)}");
                    }

                    return lines.ToString();
                case JArray array:
                    return $"shape: array, count={array.Count}, first={string.Join(" ; ", array.Take(3).Select(Preview))}";
                default:
                    return $"shape: {root.Type}: {Preview(root)}";
            }
        }

        private static T TryDeserialize<T>(string body) where T : class
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            try
            {
                return JsonConvert.DeserializeObject<T>(body);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string StringField(JObject obj, string key)
        {
            var value = (obj?[key] as JValue)?.Value as string;
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static string CharacterHref(JObject character)
        {
            var link = character?["character"] as JObject;
            return StringField(link, "href") ?? StringField(link?["key"] as JObject, "href");
        }

        private static int CountArray(string body, string key)
        {
            return (TryParse(body)?[key] as JArray)?.Count ?? 0;
        }

        private static int? ExtractTrailingId(string href)
        {
            var segment = ExtractLastSegment(href);
            return int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : (int?)null;
        }

        private static string ExtractLastSegment(string href)
        {
            if (string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(href, UriKind.Absolute, out var uri))
            {
                return null;
            }

            return uri.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault();
        }

        /// <summary>Removes every credential and account identifier the tester's settings hold.</summary>
        private static string Redact(string text, BattleNetSettings settings)
        {
            if (string.IsNullOrEmpty(text) || settings == null)
            {
                return text;
            }

            foreach (var secret in new[]
            {
                settings.BattleNetClientSecret,
                settings.BattleNetClientId,
                settings.BattleNetAccessToken,
                settings.BattleNetRefreshToken,
                settings.BattleNetAccountId,
                settings.BattleNetBattleTag,
                settings.WowCharacter
            })
            {
                if (!string.IsNullOrWhiteSpace(secret) && secret.Trim().Length >= 3)
                {
                    text = text.Replace(secret.Trim(), "<redacted>");
                    text = text.Replace(Uri.EscapeDataString(secret.Trim()), "<redacted>");
                }
            }

            return text;
        }
    }
}
