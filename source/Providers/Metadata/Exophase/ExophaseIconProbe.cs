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
        internal static readonly bool Enabled = true;

        private static readonly Guid ProbeGameId = new Guid("6c0a3c1e-1c0b-4b9e-9d0e-1c0b1c0b1c0b");

        // The reported page (a PS3 slug Exophase percent-encodes) and a control page.
        private static readonly string[] Slugs =
        {
            "steinsgate-%e7%b7%9a%e5%bd%a2%e6%8b%98%e6%9d%9f%e3%81%ae%e3%83%95%e3%82%a7%e3%83%8e%e3%82%b0%e3%83%a9%e3%83%a0-ps3",
            "hollow-knight-silksong-psn"
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
            var provider = new ExophaseMetadataProvider(ExophaseMetadataBranch.PlayStation, new ExophasePageSource(api, settings, logger), logger);
            var report = new StringBuilder();
            report.AppendLine($"Icon probe {DateTime.Now:O}");
            report.AppendLine($"List root: {disk?.ResolveCacheRelativePath(AchievementIconCachePathBuilder.BuildMetadataListsRootRelativePath())}");
            if (!await provider.BeginRunAsync(ct).ConfigureAwait(false))
            {
                report.AppendLine("Exophase is not available.");
                File.WriteAllText(Path.Combine(outputDirectory, "icon-probe.txt"), report.ToString(), Encoding.UTF8);
                return;
            }

            try
            {
                foreach (var slug in Slugs)
                {
                    report.AppendLine();
                    report.AppendLine($"== {slug}");
                    try
                    {
                        await ProbeAsync(provider, slug, icons, disk, report, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        report.AppendLine($"Probe failed: {ex}");
                    }
                }
            }
            finally
            {
                provider.EndRun();
            }

            File.WriteAllText(Path.Combine(outputDirectory, "icon-probe.txt"), report.ToString(), Encoding.UTF8);
        }

        private static async Task ProbeAsync(
            ExophaseMetadataProvider provider,
            string slug,
            AchievementIconService icons,
            DiskImageService disk,
            StringBuilder report,
            CancellationToken ct)
        {
            var set = await provider.FetchAsync(null, slug, ct).ConfigureAwait(false);
            var rows = set?.Achievements?.Where(row => row != null).ToList() ?? new List<AchievementDetail>();
            report.AppendLine($"Rows: {rows.Count}; game art: {set?.GameArtUrl ?? "(none)"}");
            foreach (var row in rows)
            {
                report.AppendLine($"  {row.ApiName} | unlocked={Show(row.UnlockedIconPath)} | locked={Show(row.LockedIconPath)}");
            }

            var folder = disk?.ResolveCacheRelativePath(
                AchievementIconCachePathBuilder.BuildMetadataListFolderRelativePath(ExophaseMetadataBranch.PlayStation.Key, slug));
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
                return;
            }

            var lists = new[] { new MetadataSourceList(slug, slug, rows) };
            var saved = await icons.PopulateMetadataSourceIconCacheAsync(ProbeGameId, ExophaseMetadataBranch.PlayStation.Key, lists, true, ct).ConfigureAwait(false);
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
