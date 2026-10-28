using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Images;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Metadata.Exophase
{
    /// <summary>
    /// Developer-only check of borrowed PlayStation icons: for a fixed list of Exophase pages,
    /// reads each page through the PlayStation branch, reports its rows' icon addresses and the
    /// list folder's path lengths, then saves the icons through the refresh's own pipeline under a
    /// probe game id and reports which rows ended up as local files. The probe's folders hold no
    /// game's icons, so the next refresh's sweep removes them.
    /// </summary>
    internal static class ExophaseIconProbe
    {
        /// <summary>Hardcoded gate for the hidden "Probe icons" menu item. Off in shipped builds.</summary>
        internal static readonly bool Enabled = false;

        private static readonly Guid ProbeGameId = new Guid("6c0a3c1e-1c0b-4b9e-9d0e-1c0b1c0b1c0b");

        // A game linked to PlayStation for icons and Xbox for points, each page read through the
        // branch it was linked on, and a control page.
        private static readonly (ExophaseMetadataBranch Branch, string Slug)[] Cases =
        {
            (ExophaseMetadataBranch.PlayStation, "007-first-light-psn"),
            (ExophaseMetadataBranch.Xbox, "007-first-light-xbox"),
            (ExophaseMetadataBranch.PlayStation, "hollow-knight-silksong-psn")
        };

        public static async Task RunAsync(
            IPlayniteAPI api,
            PlayniteAchievementsSettings settings,
            AchievementIconService icons,
            DiskImageService disk,
            ILogger logger,
            string outputDirectory,
            CancellationToken ct)
        {
            Directory.CreateDirectory(outputDirectory);
            var pages = new ExophasePageSource(api, settings, logger);
            var providers = Cases.Select(entry => entry.Branch).Distinct()
                .ToDictionary(branch => branch, branch => new ExophaseMetadataProvider(branch, pages, logger));
            var report = new StringBuilder();
            report.AppendLine($"Icon probe {DateTime.Now:O}");
            report.AppendLine($"List root: {disk?.ResolveCacheRelativePath(AchievementIconCachePathBuilder.BuildMetadataListsRootRelativePath())}");
            foreach (var provider in providers.Values)
            {
                if (!await provider.BeginRunAsync(ct).ConfigureAwait(false))
                {
                    report.AppendLine("Exophase is not available.");
                    File.WriteAllText(Path.Combine(outputDirectory, "icon-probe.txt"), report.ToString(), Encoding.UTF8);
                    return;
                }
            }

            var iconUrlsByCase = new Dictionary<string, HashSet<string>>();
            try
            {
                foreach (var (branch, slug) in Cases)
                {
                    report.AppendLine();
                    report.AppendLine($"== {branch.Key}: {slug}");
                    try
                    {
                        iconUrlsByCase[slug] = await ProbeAsync(providers[branch], branch.Key, slug, icons, disk, report, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        report.AppendLine($"Probe failed: {ex}");
                    }
                }
            }
            finally
            {
                foreach (var provider in providers.Values)
                {
                    provider.EndRun();
                }
            }

            // Whether two pages serve the same image files (query strings dropped).
            report.AppendLine();
            foreach (var first in iconUrlsByCase)
            {
                foreach (var second in iconUrlsByCase.Where(other => string.CompareOrdinal(other.Key, first.Key) > 0))
                {
                    report.AppendLine($"Shared icon files {first.Key} / {second.Key}: {first.Value.Intersect(second.Value).Count()}");
                }
            }

            File.WriteAllText(Path.Combine(outputDirectory, "icon-probe.txt"), report.ToString(), Encoding.UTF8);
        }

        private static async Task<HashSet<string>> ProbeAsync(
            ExophaseMetadataProvider provider,
            string sourceKey,
            string slug,
            AchievementIconService icons,
            DiskImageService disk,
            StringBuilder report,
            CancellationToken ct)
        {
            var set = await provider.FetchAsync(null, slug, ct).ConfigureAwait(false);
            var rows = set?.Achievements?.Where(row => row != null).ToList() ?? new List<AchievementDetail>();
            var iconUrls = new HashSet<string>(
                rows.Select(row => row.UnlockedIconPath?.Split('?')[0]).Where(url => !string.IsNullOrWhiteSpace(url)),
                StringComparer.OrdinalIgnoreCase);
            report.AppendLine($"Rows: {rows.Count}; with icons: {rows.Count(row => !string.IsNullOrWhiteSpace(row.UnlockedIconPath))}; game art: {set?.GameArtUrl ?? "(none)"}");
            foreach (var row in rows)
            {
                report.AppendLine($"  {row.ApiName} | {row.DisplayName} | unlocked={Show(row.UnlockedIconPath)} | locked={Show(row.LockedIconPath)}");
            }

            var folder = disk?.ResolveCacheRelativePath(
                AchievementIconCachePathBuilder.BuildMetadataListFolderRelativePath(sourceKey, slug));
            var firstFile = folder == null || rows.Count == 0
                ? null
                : Path.Combine(folder, Path.GetFileName(AchievementIconCachePathBuilder.BuildRelativePath(
                    ProbeGameId.ToString("D"),
                    AchievementIconCachePathBuilder.BuildFileStems(new[] { rows[0].ApiName })[rows[0].ApiName],
                    AchievementIconVariant.Unlocked)));
            report.AppendLine($"List folder ({folder?.Length ?? 0}): {folder}");
            report.AppendLine($"First file ({firstFile?.Length ?? 0}): {firstFile}");
            report.AppendLine($"Its compress temp: {(firstFile == null ? 0 : firstFile.Length + ".compress.tmp".Length)} characters");
            if (rows.Count == 0 || icons == null)
            {
                return iconUrls;
            }

            var lists = new[] { new MetadataSourceList(slug, slug, rows) };
            var saved = await icons.PopulateMetadataSourceIconCacheAsync(ProbeGameId, sourceKey, lists, true, ct).ConfigureAwait(false);
            var local = 0;
            foreach (var row in rows)
            {
                saved.TryGetValue(row, out var paths);
                var isLocal = IsLocalFile(paths.Unlocked);
                local += isLocal ? 1 : 0;
                report.AppendLine($"  saved {row.ApiName}: {(isLocal ? "local" : "NOT LOCAL")} {Show(paths.Unlocked)}");
            }

            report.AppendLine($"Saved locally: {local}/{rows.Count}");
            report.AppendLine($"Folder exists: {folder != null && Directory.Exists(folder)}; files: {(folder != null && Directory.Exists(folder) ? Directory.GetFiles(folder).Length : 0)}");
            return iconUrls;
        }

        private static bool IsLocalFile(string path)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path) && File.Exists(path);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string Show(string value) => string.IsNullOrWhiteSpace(value) ? "(blank)" : value;
    }
}
